using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Sql.Tree;

public interface IGovernedTreeQueryService
{
    /// <summary>
    /// Runs a GraphQL selection tree as one governed statement and returns the JSON array of root rows.
    /// The caller owns (disposes) the document.
    /// </summary>
    Task<JsonDocument> ExecuteAsync(
        ClaimsPrincipal? principal,
        TreeQueryNode root,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        CancellationToken ct = default);

    Task<JsonDocument> ExecuteAsync(
        ClaimsPrincipal? principal,
        TreeQueryNode root,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        string? operationId,
        CancellationToken ct = default);
}

/// <summary>
/// G2/G3/G5 (docs/plans/rls-subquery-in-strategy.md): governed execution of a GraphQL selection tree.
/// <list type="bullet">
/// <item>Access per table is resolved once per request (scoped service) through <see cref="ITableAccessResolver"/>,
/// the same decision path as OData and the table root field; one audit entry per table and request.</item>
/// <item>All tables must live in one SQL data source whose provider matches the catalog dialect (D-1).</item>
/// <item>The tree runs as ONE statement (<see cref="TreeSqlCompiler"/>); the database builds the JSON, the gateway reads
/// one text value with a size limit, parses it once and only rewrites HMAC columns.</item>
/// </list>
/// </summary>
public sealed class GovernedTreeQueryService : IGovernedTreeQueryService, IDisposable
{
    public void Dispose() => _memoLock.Dispose();

    private readonly ITableAccessResolver _accessResolver;
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IAuditLogRepository _auditLog;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly GatewayOptions _options;
    private readonly ITableReadConcurrencyGate? _concurrencyGate;
    private readonly ILogger<GovernedTreeQueryService>? _logger;
    private readonly IDbSessionContextInitializer _sessionInitializer;

    // G5 & R-GQL-3: memo and audit are keyed by operationId to isolate WebSocket operations across connection lifetime
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string OpId, TableIdentifier Table), ResolvedTableAccess> _accessByOperationAndTable = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string OpId, TableIdentifier Table, bool Allowed), byte> _auditedByOperation = new();
    private readonly SemaphoreSlim _memoLock = new(1, 1);

    private const int ThrottledRetryAfterSeconds = 2;

    public GovernedTreeQueryService(
        ITableAccessResolver accessResolver,
        ISqlConnectionFactory connectionFactory,
        IAuditLogRepository auditLog,
        IColumnMaskingProvider maskingProvider,
        IOptions<GatewayOptions> options,
        ITableReadConcurrencyGate? concurrencyGate = null,
        ILogger<GovernedTreeQueryService>? logger = null,
        IDbSessionContextInitializer? sessionInitializer = null)
    {
        _accessResolver = accessResolver ?? throw new ArgumentNullException(nameof(accessResolver));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _concurrencyGate = concurrencyGate;
        _logger = logger;
        _sessionInitializer = sessionInitializer ?? new DbSessionContextInitializer();
    }

    public Task<JsonDocument> ExecuteAsync(
        ClaimsPrincipal? principal,
        TreeQueryNode root,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        CancellationToken ct = default) =>
        ExecuteAsync(principal, root, requestHeaders, operationId: null, ct);

    public async Task<JsonDocument> ExecuteAsync(
        ClaimsPrincipal? principal,
        TreeQueryNode root,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        string? operationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);

        operationId ??= Guid.NewGuid().ToString("N");

        var maxRows = _options.GraphQL?.MaxResponseRows > 0 ? _options.GraphQL.MaxResponseRows : 1000;
        if (root.Limit > maxRows)
        {
            throw new GatewayInvalidQueryException($"The page size must not exceed {maxRows}.");
        }

        // 1. Access per table (memoized per operation), denied tables fail before any database access.
        var columnsByTable = new Dictionary<TableIdentifier, List<string>>();
        CollectColumns(root, columnsByTable);

        var resolved = new Dictionary<TableIdentifier, ResolvedTableAccess>();
        foreach (var (table, columns) in columnsByTable)
        {
            var access = await ResolveOnceAsync(operationId, principal, table, columns, requestHeaders, ct).ConfigureAwait(false);
            await AuditOnceAsync(operationId, access, table, ct).ConfigureAwait(false);
            if (!access.Decision.IsAllowed)
            {
                throw new GatewayForbiddenException("Access denied.");
            }
            resolved[table] = access;
        }

        // 2. One SQL data source, provider dialect == catalog dialect.
        var rootAccess = resolved[root.Table];
        var sourceName = rootAccess.Metadata.Table.SourceName;
        foreach (var access in resolved.Values)
        {
            if (access.Metadata.DataSourceType != DataSourceType.Sql)
            {
                throw new GatewayInvalidQueryException($"The table '{access.Metadata.Identifier}' is not a SQL table and cannot be queried with relations.");
            }
            if (!string.Equals(access.Metadata.Table.SourceName, sourceName, StringComparison.OrdinalIgnoreCase))
            {
                throw new GatewayInvalidQueryException("Relations across data sources are not supported in one query.");
            }
        }

        var connections = _options.DataSources?.Connections;
        if (connections == null || !connections.TryGetValue(sourceName, out var connOptions) || string.IsNullOrWhiteSpace(connOptions.ConnectionString))
        {
            throw new InvalidOperationException($"The data source '{sourceName}' has no database connection.");
        }

        var provider = string.IsNullOrWhiteSpace(connOptions.Provider) ? "sqlite" : connOptions.Provider.Trim();
        if (!DatabaseDialectExtensions.TryParseDialect(provider, out var dialect))
        {
            throw new InvalidOperationException($"The provider '{provider}' of data source '{sourceName}' is not supported.");
        }
        foreach (var access in resolved.Values)
        {
            if (access.Metadata.Dialect != dialect)
            {
                throw new InvalidOperationException($"Catalog dialect '{access.Metadata.Dialect}' of '{access.Metadata.Identifier}' does not match the provider '{provider}' of data source '{sourceName}'.");
            }
        }

        // 3. Compile (tenant filter per table, masks, row filters).
        var treeAccess = new Dictionary<TableIdentifier, TreeTableAccess>();
        foreach (var (table, access) in resolved)
        {
            var tenantColumn = TableMetadata.RequireTenantColumnOrThrow(
                access.Metadata,
                _options.DataSources?.RequireTenantColumn == true,
                _options.DataSources?.TenantColumnExemptTables);
            treeAccess[table] = new TreeTableAccess(access.Metadata, access.Decision, tenantColumn, tenantColumn == null ? null : access.Tenant.Value);
        }

        var compiled = TreeSqlCompiler.Compile(root, treeAccess, dialect, _options.IsColumnMaskingDisabled);

        // 4. Bounded concurrency per user and root table, then one round trip.
        using var lease = AcquireLease(rootAccess);
        var json = await RunAsync(compiled, connOptions, dialect, rootAccess.Tenant, rootAccess.UserSid.Value, purpose: null, ct).ConfigureAwait(false);

        // 5. Parse once; rewrite only HMAC columns.
        if (compiled.HmacColumns.Count == 0)
        {
            return JsonDocument.Parse(json);
        }

        var node = JsonNode.Parse(json) ?? new JsonArray();
        var tenant = rootAccess.Tenant.Value;
        foreach (var hmac in compiled.HmacColumns)
        {
            var rule = MaskingRule.CreateTenantScopedHmacRule(hmac.Rule, tenant, _options.DataMasking?.HmacKeyId);
            Pseudonymize(node, hmac.Path, 0, hmac.Column, rule);
        }

        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            node.WriteTo(writer);
        }
        stream.Position = 0;
        return JsonDocument.Parse(stream);
    }

    private static void CollectColumns(TreeQueryNode node, Dictionary<TableIdentifier, List<string>> columnsByTable)
    {
        if (!columnsByTable.TryGetValue(node.Table, out var columns))
        {
            columns = [];
            columnsByTable[node.Table] = columns;
        }
        columns.AddRange(node.Columns.Where(c => !columns.Contains(c, StringComparer.OrdinalIgnoreCase)));
        foreach (var relation in node.Relations)
        {
            CollectColumns(relation.Child, columnsByTable);
        }
    }

    private async Task<ResolvedTableAccess> ResolveOnceAsync(
        string opId,
        ClaimsPrincipal? principal,
        TableIdentifier table,
        IReadOnlyList<string> columns,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        CancellationToken ct)
    {
        if (_accessByOperationAndTable.TryGetValue((opId, table), out var cached))
        {
            return cached;
        }

        await _memoLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_accessByOperationAndTable.TryGetValue((opId, table), out cached))
            {
                return cached;
            }
            var access = await _accessResolver.ResolveTableAccessAsync(principal, table, columns, requestHeaders, ct).ConfigureAwait(false);
            _accessByOperationAndTable[(opId, table)] = access;
            return access;
        }
        finally
        {
            _memoLock.Release();
        }
    }

    private async Task AuditOnceAsync(string opId, ResolvedTableAccess access, TableIdentifier table, CancellationToken ct)
    {
        if (!_auditedByOperation.TryAdd((opId, table, access.Decision.IsAllowed), 1))
        {
            return;
        }

        await _auditLog.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = access.Tenant,
            EventType = "TABLE_QUERY",
            ActorSid = access.UserSid,
            TargetTable = table.ToString(),
            Decision = access.Decision.IsAllowed ? "ALLOW" : "DENY",
            TraceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId(),
            DetailsJson = JsonSerializer.Serialize(new { is_allowed = access.Decision.IsAllowed, reasons = access.Decision.DeniedReasons, path = "graphql_tree" })
        }, ct).ConfigureAwait(false);
    }

    private IDisposable? AcquireLease(ResolvedTableAccess rootAccess)
    {
        var max = _options.DataSources?.MaxConcurrentReadsPerUserAndTable ?? 0;
        if (_concurrencyGate == null || max <= 0)
        {
            return null;
        }
        var key = $"{rootAccess.Tenant.Value}|{rootAccess.UserSid.Value}|{rootAccess.Metadata.Identifier.ToQualifiedName()}".ToLowerInvariant();
        return _concurrencyGate.TryEnter(key, max) ?? throw new GatewayThrottledException(ThrottledRetryAfterSeconds);
    }

    private async Task<string> RunAsync(
        CompiledTreeQuery compiled,
        DataSourceConnectionOptions connOptions,
        DatabaseDialect dialect,
        TenantId tenant,
        string? userSid,
        string? purpose,
        CancellationToken ct)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(connOptions, ct).ConfigureAwait(false);
        DbTransaction? tx = null;
        try
        {
            tx = await _sessionInitializer.InitializeSessionAsync(
                connection,
                dialect,
                tenant,
                userSid: userSid,
                purpose: purpose,
                requireTransaction: true,
                ct: ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = compiled.Sql;
            command.CommandTimeout = Math.Max(1, connOptions.CommandTimeoutSeconds);
            foreach (var (name, value) in compiled.Parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            if (_options.Logging?.LogGeneratedSql == true)
            {
                _logger?.LogDebug("GovernedTreeQueryService: Generated SQL: {Sql}", compiled.Sql);
            }

            string json;
            await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleRow, ct).ConfigureAwait(false))
            {
                json = await reader.ReadAsync(ct).ConfigureAwait(false) && !await reader.IsDBNullAsync(0, ct).ConfigureAwait(false)
                    ? await ReadBoundedTextAsync(reader, MaxResponseBytes(), ct).ConfigureAwait(false)
                    : "[]";
            }

            if (tx != null)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            return json;
        }
        catch (Exception)
        {
            if (tx != null)
            {
                try
                {
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rbEx)
                {
                    _logger?.LogWarning(rbEx, "Failed to rollback transaction after error in GovernedTreeQueryService");
                }
            }
            throw;
        }
        finally
        {
            if (tx != null)
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private long MaxResponseBytes() =>
        _options.GraphQL?.MaxResponseBytes > 0 ? _options.GraphQL.MaxResponseBytes : 10 * 1024 * 1024;

    /// <summary>O11: reads the JSON text in chunks and stops at the byte limit (UTF-16 estimate, as elsewhere).</summary>
    private static async Task<string> ReadBoundedTextAsync(DbDataReader reader, long maxBytes, CancellationToken ct)
    {
        using var textReader = reader.GetTextReader(0);
        var builder = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await textReader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            builder.Append(buffer, 0, read);
            if (builder.Length * 2L > maxBytes)
            {
                throw new GatewaySecurityException($"Antwortgröße überschreitet das konfigurierte Limit von {maxBytes} Bytes.", "RESPONSE_TOO_LARGE");
            }
        }
        return builder.ToString();
    }

    private void Pseudonymize(JsonNode? node, IReadOnlyList<string> path, int index, string column, MaskingRule rule)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    Pseudonymize(item, path, index, column, rule);
                }
                break;
            case JsonObject obj when index < path.Count:
                Pseudonymize(obj[path[index]], path, index + 1, column, rule);
                break;
            case JsonObject obj:
                if (obj.TryGetPropertyValue(column, out var value) && value != null)
                {
                    var raw = value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();
                    var masked = _maskingProvider.MaskValue(column, raw, rule);
                    obj[column] = masked == null ? null : JsonValue.Create(Convert.ToString(masked, System.Globalization.CultureInfo.InvariantCulture));
                }
                break;
        }
    }
}
