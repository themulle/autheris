namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Security;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.Synthetic;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine.Analysis;

/// <summary>
/// AR-05: Dedicated execution engine for governed WebSQL queries and DML statements.
/// Handles connection management, session initialization, concurrency gating, audit recording, and result buffering.
/// </summary>
public sealed class GovernedSqlExecutor
{
    private const string InternalParameterPrefix = GovernedSqlExecutionService.InternalParameterPrefix;
    private const int ThrottledRetryAfterSeconds = 2;

    private readonly GovernedSqlRewriter _rewriter;
    private readonly IOptions<GatewayOptions> _options;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly ISqlConnectionFactory? _connectionFactory;
    private readonly IHostEnvironment? _environment;
    private readonly ILogger? _logger;
    private readonly ITableReadConcurrencyGate? _concurrencyGate;
    private readonly IDbSessionContextInitializer _sessionInitializer;
    private readonly SqlDataSourceResolver _dataSourceResolver;

    public GovernedSqlExecutor(
        GovernedSqlRewriter rewriter,
        IOptions<GatewayOptions> options,
        IAuditLogRepository auditLogRepository,
        ISqlConnectionFactory? connectionFactory = null,
        IHostEnvironment? environment = null,
        ILogger? logger = null,
        ITableReadConcurrencyGate? concurrencyGate = null,
        IDbSessionContextInitializer? sessionInitializer = null)
    {
        _rewriter = rewriter ?? throw new ArgumentNullException(nameof(rewriter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _connectionFactory = connectionFactory;
        _environment = environment;
        _logger = logger;
        _concurrencyGate = concurrencyGate;
        _sessionInitializer = sessionInitializer ?? new DbSessionContextInitializer();
        _dataSourceResolver = new SqlDataSourceResolver(_options, logger);
    }

    public async Task ExecuteGovernedQueryAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        Func<DbDataReader, CancellationToken, Task> rowWriter,
        CancellationToken ct = default)
    {
        await ExecuteCoreAsync(request, user, tenantId, rowWriter, ct).ConfigureAwait(false);
    }

    public async Task<GovernedSqlResult> ExecuteQueryBufferedAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var columns = new List<string>();
        IReadOnlyList<SqlResultColumn> columnDescriptions = Array.Empty<SqlResultColumn>();
        bool isTruncated = false;
        var sw = Stopwatch.StartNew();

        string securedSql = await ExecuteCoreAsync(
            request,
            user,
            tenantId,
            async (reader, token) =>
            {
                columnDescriptions = SqlResultColumns.Describe(reader);
                foreach (var column in columnDescriptions)
                {
                    columns.Add(column.Name);
                }

                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }
                    rows.Add(row);
                }

                isTruncated = reader is RowLimitedDataReader { HasMoreRows: true };
            },
            ct).ConfigureAwait(false);

        sw.Stop();

        return new GovernedSqlResult(
            OriginalSql: request.Sql,
            RewrittenSql: securedSql,
            Columns: columns.AsReadOnly(),
            Rows: rows.AsReadOnly(),
            RowCount: rows.Count,
            ElapsedMilliseconds: sw.ElapsedMilliseconds,
            Truncated: isTruncated,
            ColumnDescriptions: columnDescriptions);
    }

    private async Task<string> ExecuteCoreAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        Func<DbDataReader, CancellationToken, Task> rowWriter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rowWriter);

        string? requestedDs = !string.IsNullOrWhiteSpace(request.DataSourceName)
            ? _dataSourceResolver.ResolveAllowedDataSource(request.DataSourceName, tenantId)
            : null;

        if (request.Parameters != null)
        {
            foreach (var paramName in request.Parameters.Keys)
            {
                var trimmed = paramName.TrimStart('@');
                if (trimmed.StartsWith(InternalParameterPrefix, StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("p_rls_", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("rls_", StringComparison.OrdinalIgnoreCase))
                {
                    throw new WebSqlPolicyException("A request parameter uses a reserved gateway parameter name.");
                }
            }
        }

        string sqlForRewrite = GovernedSqlExecutionService.NormalizeClientParameters(request.Sql, request.Parameters, out var clientParameterNames);

        var dmlContext = new DmlAuditContext();
        GovernedRewrite rewrite;
        try
        {
            rewrite = await _rewriter.RewriteCoreAsync(sqlForRewrite, user, tenantId, requestedDs, dmlContext, ct, request.RowLimit, probeExtraRow: true).ConfigureAwait(false);
        }
        catch (SecurityException policyEx)
        {
            string auditDs = requestedDs ?? _options.Value.WebSql.DefaultDataSourceName;
            if (dmlContext.IsDml)
            {
                await RecordDmlAuditAsync(tenantId, user, auditDs, dmlContext, "WEBSQL_DML_REJECTED", "DENY", request.Sql, affectedRows: null, reason: policyEx is WebSqlPolicyException ? policyEx.Message : "policy violation", synthetic: false, ct).ConfigureAwait(false);
            }
            else if (_auditLogRepository != null)
            {
                var (redactedOriginalSql, originalSqlHash) = TrinoSqlEngine.Ast.Visitors.AstSecurityVisitor.AnonymizeSqlForAudit(request.Sql);
                var userSid = user.GetUserSid() ?? new Sid("anonymous");
                var detailsJson = new AuditDetailsBuilder()
                    .WithField("originalSql", redactedOriginalSql)
                    .WithField("originalSqlHash", originalSqlHash)
                    .WithField("dataSource", auditDs)
                    .WithField("reasonCode", policyEx is WebSqlPolicyException ? "POLICY_VIOLATION" : "SECURITY_VIOLATION")
                    .WithField("error", policyEx.Message)
                    .Build();

                await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
                {
                    TenantId = tenantId,
                    EventType = AuditEventTypes.WebSqlQueryDenied,
                    ActorSid = userSid,
                    TargetTable = auditDs,
                    Decision = "DENY",
                    TraceId = Guid.NewGuid().ToString("N"),
                    DetailsJson = detailsJson
                }, ct).ConfigureAwait(false);
            }
            throw;
        }

        string securedSql = GovernedSqlExecutionService.RestoreClientParameters(rewrite.Sql, clientParameterNames);
        string dsName = rewrite.DataSourceName;

        if (_auditLogRepository != null && !dmlContext.IsDml)
        {
            var (redactedOriginalSql, originalSqlHash) = TrinoSqlEngine.Ast.Visitors.AstSecurityVisitor.AnonymizeSqlForAudit(request.Sql);
            var (redactedSecuredSql, _) = TrinoSqlEngine.Ast.Visitors.AstSecurityVisitor.AnonymizeSqlForAudit(securedSql);
            var userSid = user.GetUserSid() ?? new Sid("anonymous");
            await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = "WEBSQL_QUERY",
                ActorSid = userSid,
                TargetTable = dsName,
                Decision = "ALLOW",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    originalSql = redactedOriginalSql,
                    originalSqlHash,
                    securedSql = redactedSecuredSql,
                    dataSource = dsName,
                    virtual_filters = rewrite.VirtualFilters
                })
            }, ct).ConfigureAwait(false);
        }

        var connOptions = _dataSourceResolver.ResolveConnectionOptions(dsName);

        if (connOptions == null || string.IsNullOrWhiteSpace(connOptions.ConnectionString) || _connectionFactory == null)
        {
            bool isDev = _environment != null &&
                         string.Equals(_environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);
            bool isExplicitlyAllowed = _options.Value.AreExternalSystemsMockedIfUnreachable;

            if (!isDev && !isExplicitlyAllowed)
            {
                throw new GatewayNotImplementedException($"No active database connection configured for data source '{dsName}'. Synthetic fallback is disabled outside Development.");
            }

            if (dmlContext.IsDml)
            {
                await RecordDmlAuditAsync(tenantId, user, dsName, dmlContext, "WEBSQL_DML_EXECUTED", "ALLOW", securedSql, affectedRows: 0, reason: null, synthetic: true, ct).ConfigureAwait(false);
            }

            using var syntheticReader = new SyntheticDataTableReader(securedSql);
            await rowWriter(syntheticReader, ct).ConfigureAwait(false);
            return securedSql;
        }

        var maxConcurrentReads = _options.Value.DataSources?.MaxConcurrentReadsPerUserAndTable ?? 0;
        List<IDisposable>? leases = null;
        if (_concurrencyGate != null && maxConcurrentReads > 0 && rewrite.AccessedTables.Count > 0)
        {
            var userSid = user.FindFirst(ClaimTypes.PrimarySid)?.Value
                          ?? user.FindFirst("sub")?.Value
                          ?? "anonymous";
            leases = new List<IDisposable>(rewrite.AccessedTables.Count);
            try
            {
                foreach (var table in rewrite.AccessedTables)
                {
                    var key = $"{tenantId.Value}|{userSid}|{table.ToQualifiedName()}".ToLowerInvariant();
                    var lease = _concurrencyGate.TryEnter(key, maxConcurrentReads)
                                ?? throw new GatewayThrottledException(ThrottledRetryAfterSeconds);
                    leases.Add(lease);
                }
            }
            catch
            {
                foreach (var lease in leases)
                {
                    lease.Dispose();
                }
                throw;
            }
        }

        DbTransaction? tx = null;
        bool txCommitted = false;
        try
        {
            await using var connection = await _connectionFactory.CreateOpenConnectionAsync(connOptions, ct).ConfigureAwait(false);

            if (GovernedSqlExecutionService.TryMapProviderToDialect(connOptions.Provider, out var connectionDialect))
            {
                var userSid = user.FindFirst(ClaimTypes.PrimarySid)?.Value
                              ?? user.FindFirst("sub")?.Value;

                tx = await _sessionInitializer.InitializeSessionAsync(
                    connection,
                    connectionDialect,
                    tenantId,
                    userSid: userSid,
                    purpose: null,
                    requireTransaction: true,
                    ct: ct).ConfigureAwait(false);
            }

            await using var command = connection.CreateCommand();
            if (tx != null)
            {
                command.Transaction = tx;
            }
            command.CommandText = securedSql;
            command.CommandTimeout = Math.Max(1, _options.Value.WebSql.ExecutionTimeoutSeconds);

            foreach (var (paramName, paramVal) in rewrite.InternalParameters)
            {
                var p = command.CreateParameter();
                p.ParameterName = paramName.StartsWith('@') ? paramName : "@" + paramName;
                p.Value = paramVal ?? DBNull.Value;
                command.Parameters.Add(p);
            }

            if (request.Parameters != null)
            {
                var internalParamNames = new HashSet<string>(
                    rewrite.InternalParameters.Keys.Select(k => k.StartsWith('@') ? k : "@" + k),
                    StringComparer.OrdinalIgnoreCase);

                var boundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (paramName, paramVal) in request.Parameters)
                {
                    string normalizedName = paramName.StartsWith('@') ? paramName : "@" + paramName;
                    if (internalParamNames.Contains(normalizedName))
                    {
                        throw new WebSqlPolicyException($"Client parameter '{paramName}' collides with an internal security rewrite parameter.");
                    }

                    if (!boundNames.Add(normalizedName))
                    {
                        continue;
                    }

                    var p = command.CreateParameter();
                    p.ParameterName = normalizedName;
                    p.Value = WebSqlParameterValues.Normalize(paramVal) ?? DBNull.Value;
                    command.Parameters.Add(p);
                }
            }

            if (dmlContext.IsDml)
            {
                await ExecuteDmlInTransactionAsync(connection, command, tx, tenantId, user, dsName, dmlContext, securedSql, ct).ConfigureAwait(false);
                txCommitted = true;

                using var emptyTable = new DataTable();
                using var emptyReader = emptyTable.CreateDataReader();
                await rowWriter(emptyReader, ct).ConfigureAwait(false);
                return securedSql;
            }

            await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false))
            {
                if (rewrite.DeliveredRowLimit > 0)
                {
                    await rowWriter(new RowLimitedDataReader(reader, rewrite.DeliveredRowLimit), ct).ConfigureAwait(false);
                }
                else
                {
                    await rowWriter(reader, ct).ConfigureAwait(false);
                }
            }

            if (tx != null)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
                txCommitted = true;
            }
            return securedSql;
        }
        catch (Exception ex)
        {
            if (_auditLogRepository != null && !dmlContext.IsDml)
            {
                try
                {
                    var (redactedOriginalSql, originalSqlHash) = TrinoSqlEngine.Ast.Visitors.AstSecurityVisitor.AnonymizeSqlForAudit(request.Sql);
                    var userSid = user.GetUserSid() ?? new Sid("anonymous");
                    var detailsJson = new AuditDetailsBuilder()
                        .WithField("originalSql", redactedOriginalSql)
                        .WithField("originalSqlHash", originalSqlHash)
                        .WithField("dataSource", dsName)
                        .WithField("reasonCode", (ex is OperationCanceledException && ct.IsCancellationRequested) ? "CLIENT_ABORTED" : ex.GetType().Name)
                        .WithField("error", ex.Message)
                        .Build();

                    await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
                    {
                        TenantId = tenantId,
                        EventType = AuditEventTypes.QueryExecutionError,
                        ActorSid = userSid,
                        TargetTable = dsName,
                        Decision = "ERROR",
                        TraceId = Guid.NewGuid().ToString("N"),
                        DetailsJson = detailsJson
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            if (tx != null && !txCommitted)
            {
                try
                {
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rbEx)
                {
                    _logger?.LogWarning(rbEx, "Failed to rollback WebSQL transaction.");
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
            if (leases != null)
            {
                foreach (var lease in leases)
                {
                    lease.Dispose();
                }
            }
        }
    }

    private async Task ExecuteDmlInTransactionAsync(
        DbConnection connection,
        DbCommand command,
        DbTransaction? existingTx,
        TenantId tenantId,
        ClaimsPrincipal user,
        string dsName,
        DmlAuditContext dmlContext,
        string securedSql,
        CancellationToken ct)
    {
        bool ownsTx = false;
        DbTransaction transaction;
        if (existingTx != null)
        {
            transaction = existingTx;
        }
        else
        {
            transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            command.Transaction = transaction;
            ownsTx = true;
        }

        long maxAffectedRows = _options.Value.WebSql.MaxAffectedRows;
        int affectedRows;
        try
        {
            try
            {
                affectedRows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rbEx)
                {
                    _logger?.LogWarning(rbEx, "Failed to rollback WebSQL DML transaction.");
                }

                await RecordDmlAuditAsync(tenantId, user, dsName, dmlContext, "WEBSQL_DML_FAILED", "DENY", securedSql, affectedRows: null, reason: ex.GetType().Name, synthetic: false, ct).ConfigureAwait(false);
                throw;
            }

            if (maxAffectedRows > 0 && (affectedRows < 0 || affectedRows > maxAffectedRows))
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rbEx)
                {
                    _logger?.LogWarning(rbEx, "Failed to rollback WebSQL DML transaction after row limit exceeded.");
                }

                await RecordDmlAuditAsync(tenantId, user, dsName, dmlContext, "WEBSQL_DML_REJECTED", "DENY", securedSql, affectedRows, reason: "MaxAffectedRows exceeded; rolled back", synthetic: false, ct).ConfigureAwait(false);

                throw new WebSqlPolicyException(affectedRows < 0
                    ? "The number of rows affected by the DML statement could not be verified against WebSql.MaxAffectedRows. The statement was rolled back."
                    : $"The DML statement affected {affectedRows} rows, which exceeds the configured limit of {maxAffectedRows} (WebSql.MaxAffectedRows). The statement was rolled back.");
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            await RecordDmlAuditAsync(tenantId, user, dsName, dmlContext, "WEBSQL_DML_EXECUTED", "ALLOW", securedSql, affectedRows, reason: null, synthetic: false, ct).ConfigureAwait(false);
        }
        finally
        {
            if (ownsTx)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task RecordDmlAuditAsync(
        TenantId tenantId,
        ClaimsPrincipal user,
        string dsName,
        DmlAuditContext dmlContext,
        string eventType,
        string decision,
        string sqlForHash,
        int? affectedRows,
        string? reason,
        bool synthetic,
        CancellationToken ct)
    {
        if (_auditLogRepository == null)
        {
            return;
        }

        string sqlHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sqlForHash)));
        await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = eventType,
            ActorSid = user.GetUserSid() ?? new Sid("anonymous"),
            TargetTable = string.Join(",", dmlContext.Tables),
            Decision = decision,
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                statementType = dmlContext.StatementType?.ToString(),
                tables = dmlContext.Tables,
                dataSource = dsName,
                affectedRows,
                maxAffectedRows = _options.Value.WebSql.MaxAffectedRows,
                sqlSha256 = sqlHash,
                reason,
                synthetic
            })
        }, ct).ConfigureAwait(false);
    }
}
