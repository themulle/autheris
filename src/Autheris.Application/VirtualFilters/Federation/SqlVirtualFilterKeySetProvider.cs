namespace Autheris.Application.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-GOV-14 (M4; C-3, C-4; SEC-4, SEC-5, SEC-6, SEC-10, SEC-11): loads the authorized key set of a structured Virtual
/// Filter from its SQL key source.
/// <list type="bullet">
/// <item>Only structured filters without validity window whose FROM and JOIN tables are SQL tables of one data source
/// in the catalog; anything else denies with <c>VF_KEY_SOURCE_UNSUPPORTED</c>.</item>
/// <item>The complete definition runs in SQL (<see cref="StructuredFilterKeyQueryBuilder"/>), values and tenant bound
/// as parameters; every table with a tenant column is restricted to the caller's tenant, a table without one is
/// refused when <c>DataSources:RequireTenantColumn</c> is on and it is not exempt.</item>
/// <item>At most <c>MaxAuthorizedKeys + 1</c> rows are read; one more than the maximum denies with
/// <c>VF_KEY_SET_TOO_LARGE</c> (never truncated).</item>
/// <item>The governed read connection of the data source (<c>DataSources:Connections</c>), a read-only transaction on
/// PostgreSQL and the key query timeout; source errors deny with <c>VIRTUAL_FILTER_SOURCE_UNAVAILABLE</c> without
/// the database message (attached as inner exception only).</item>
/// <item>Cached per tenant, filter id, definition hash, key columns and key types in <see cref="VirtualFilterKeySetCache"/>.</item>
/// </list>
/// </summary>
public sealed class SqlVirtualFilterKeySetProvider : IVirtualFilterKeySetProvider
{
    private readonly ITableMetadataRepository _tables;
    private readonly ISqlConnectionFactory _connections;
    private readonly IOptions<GatewayOptions> _options;
    private readonly VirtualFilterKeySetCache _cache;
    private readonly ILogger<SqlVirtualFilterKeySetProvider>? _logger;
    private readonly IDbSessionContextInitializer _sessionInitializer;

    public SqlVirtualFilterKeySetProvider(
        ITableMetadataRepository tables,
        ISqlConnectionFactory connections,
        IOptions<GatewayOptions> options,
        VirtualFilterKeySetCache cache,
        ILogger<SqlVirtualFilterKeySetProvider>? logger = null,
        IDbSessionContextInitializer? sessionInitializer = null)
    {
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger;
        _sessionInitializer = sessionInitializer ?? new DbSessionContextInitializer();
        Federation.Validate();
    }

    private VirtualFilterFederationOptions Federation => _options.Value.VirtualFilters?.Federation ?? new VirtualFilterFederationOptions();

    public async ValueTask<VirtualFilterKeySet> GetAsync(
        VirtualFilter filter,
        IReadOnlyList<VirtualFilterKeyType> keyTypes,
        TenantId tenant,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(keyTypes);
        EnsureSupported(filter, keyTypes, tenant);

        var federation = Federation;
        var cacheKey = string.Join(':',
            "vfkeys",
            tenant.Value,
            filter.Id.ToString("N"),
            filter.ComputeDefinitionHash(),
            string.Join(',', filter.KeyColumns),
            string.Join(',', keyTypes.Select(t => (int)t)));

        var set = await _cache.GetOrLoadAsync(
            cacheKey,
            TimeSpan.FromSeconds(federation.KeyCacheTtlSeconds),
            loadCt => LoadAsync(filter, keyTypes, tenant, federation, loadCt),
            ct,
            TimeSpan.FromSeconds(federation.KeyQueryTimeoutSeconds)).ConfigureAwait(false);

        if (set.Count > federation.MaxAuthorizedKeys)
        {
            throw TooLarge(filter, federation.MaxAuthorizedKeys);
        }

        return set;
    }

    private static void EnsureSupported(VirtualFilter filter, IReadOnlyList<VirtualFilterKeyType> keyTypes, TenantId tenant)
    {
        if (filter.Sql != null || filter.Structured == null)
        {
            throw Unsupported(filter, "it is not a structured filter");
        }

        if (filter.ValidFromColumn != null || filter.ValidToColumn != null)
        {
            throw Unsupported(filter, "it has a validity window");
        }

        if (filter.TenantId != tenant)
        {
            throw Unsupported(filter, "it belongs to another tenant");
        }

        try
        {
            // SEC-23: the repository load path does not validate; every identifier used below must be safe.
            filter.Validate();
        }
        catch (ArgumentException)
        {
            throw Unsupported(filter, "its definition is invalid");
        }

        if (keyTypes.Count != filter.KeyColumns.Count)
        {
            throw new GatewaySecurityException(
                $"The Virtual Filter '{filter.Name}' has {filter.KeyColumns.Count} key columns, the protected object {keyTypes.Count}.",
                VirtualFilterErrorCodes.KeyTypeMismatch);
        }
    }

    private async Task<VirtualFilterKeySet> LoadAsync(
        VirtualFilter filter,
        IReadOnlyList<VirtualFilterKeyType> keyTypes,
        TenantId tenant,
        VirtualFilterFederationOptions federation,
        CancellationToken ct)
    {
        List<object?[]> rows;
        try
        {
            var (connectionName, dialect, tenantColumns) = await ResolveKeySourceAsync(filter, ct).ConfigureAwait(false);
            var connections = _options.Value.DataSources?.Connections;
            if (connections == null || !connections.TryGetValue(connectionName, out var connection) || string.IsNullOrWhiteSpace(connection.ConnectionString))
            {
                throw Unsupported(filter, "its data source has no governed read connection");
            }

            if (!DataSourceProvider.TryResolveDialect(connection.Provider, out var connectionDialect) || connectionDialect != dialect)
            {
                throw Unsupported(filter, "the dialect of its data source connection does not match the catalog");
            }

            var query = StructuredFilterKeyQueryBuilder.Build(filter, dialect, tenantColumns, tenant.Value, federation.MaxAuthorizedKeys + 1);
            rows = await ReadAsync(connection, dialect, query, keyTypes.Count, tenant, federation.KeyQueryTimeoutSeconds, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not GatewaySecurityException)
        {
            // SEC-11: database messages can quote data values; only the type is logged, the exception is attached.
            _logger?.LogWarning("Virtual filter key source of '{Filter}' could not be read ({ExceptionType}); denied.", filter.Name, ex.GetType().Name);
            throw new GatewaySecurityException(
                $"The key source of the Virtual Filter '{filter.Name}' is unavailable.", VirtualFilterErrorCodes.SourceUnavailable, ex);
        }

        if (rows.Count > federation.MaxAuthorizedKeys)
        {
            throw TooLarge(filter, federation.MaxAuthorizedKeys);
        }

        var set = VirtualFilterKeySet.Create(keyTypes, rows);
        if (set.DroppedKeyCount > 0)
        {
            _logger?.LogWarning("Virtual filter '{Filter}': {Dropped} key tuples of the key source could not be converted and authorize nothing.", filter.Name, set.DroppedKeyCount);
        }

        return set;
    }

    /// <summary>Catalog entries of the FROM and JOIN tables: one SQL data source; tenant column per alias (WI-19).</summary>
    private async Task<(string ConnectionName, DatabaseDialect Dialect, IReadOnlyDictionary<string, string> TenantColumns)> ResolveKeySourceAsync(
        VirtualFilter filter, CancellationToken ct)
    {
        var definition = filter.Structured!;
        var tables = new[] { (definition.From, definition.FromAlias) }.Concat(definition.Joins.Select(j => (j.Table, j.Alias)));
        var dataSources = _options.Value.DataSources;
        var tenantColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? connectionName = null;
        DatabaseDialect? dialect = null;
        foreach (var (identifier, alias) in tables)
        {
            var metadata = await _tables.GetTableMetadataAsync(identifier, ct).ConfigureAwait(false);
            if (metadata?.Table == null || metadata.Table.DataSourceType != DataSourceType.Sql)
            {
                throw Unsupported(filter, $"the table '{identifier}' is not a SQL table in the catalog");
            }

            var name = !string.IsNullOrWhiteSpace(metadata.Table.SourceName) ? metadata.Table.SourceName : identifier.Domain;
            DatabaseDialect tableDialect;
            try
            {
                tableDialect = metadata.Dialect;
            }
            catch (NotSupportedException)
            {
                throw Unsupported(filter, $"the table '{identifier}' has no supported dialect");
            }

            if ((connectionName != null && !string.Equals(connectionName, name, StringComparison.OrdinalIgnoreCase)) ||
                (dialect != null && dialect != tableDialect))
            {
                throw Unsupported(filter, "its tables are in different data sources");
            }

            connectionName = name;
            dialect = tableDialect;

            string? tenantColumn;
            try
            {
                tenantColumn = TableMetadata.RequireTenantColumnOrThrow(metadata, dataSources?.RequireTenantColumn == true, dataSources?.TenantColumnExemptTables);
            }
            catch (System.Security.SecurityException)
            {
                throw Unsupported(filter, $"the table '{identifier}' has no tenant column and is not exempt");
            }

            if (tenantColumn != null)
            {
                tenantColumns[alias] = tenantColumn;
            }
        }

        if (dialect is not (DatabaseDialect.SqlServer or DatabaseDialect.PostgreSql or DatabaseDialect.Sqlite))
        {
            throw Unsupported(filter, "its data source dialect is not supported");
        }

        return (connectionName!, dialect.Value, tenantColumns);
    }

    private async Task<List<object?[]>> ReadAsync(
        DataSourceConnectionOptions options,
        DatabaseDialect dialect,
        VirtualFilterKeyQuery query,
        int keyCount,
        TenantId tenant,
        int timeoutSeconds,
        CancellationToken ct)
    {
        await using var connection = await _connections.CreateOpenConnectionAsync(options, ct).ConfigureAwait(false);

        // PostgreSQL: read-only transaction (the key query must not change data), never committed.
        await using DbTransaction? tx = dialect == DatabaseDialect.PostgreSql
            ? await connection.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        if (tx != null)
        {
            await using var readOnly = connection.CreateCommand();
            readOnly.Transaction = tx;
            readOnly.CommandText = "SET TRANSACTION READ ONLY";
            await readOnly.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Principal-independent key set: the session carries the tenant only.
        await _sessionInitializer.InitializeSessionAsync(connection, tx, dialect, tenant, userSid: null, purpose: null, ct: ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandType = CommandType.Text;
        cmd.CommandTimeout = timeoutSeconds;
        cmd.CommandText = query.Sql;
        foreach (var (name, value) in query.Parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        var rows = new List<object?[]>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = new object?[keyCount];
                for (int i = 0; i < keyCount; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }
        }

        if (tx != null)
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return rows;
    }

    private static GatewaySecurityException Unsupported(VirtualFilter filter, string why) =>
        new($"The Virtual Filter '{filter.Name}' cannot be evaluated in its key source: {why}.", VirtualFilterErrorCodes.KeySourceUnsupported);

    private static GatewaySecurityException TooLarge(VirtualFilter filter, int max) =>
        new($"The Virtual Filter '{filter.Name}' authorizes more than {max} keys.", VirtualFilterErrorCodes.KeySetTooLarge);
}
