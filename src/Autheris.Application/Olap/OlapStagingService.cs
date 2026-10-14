namespace Autheris.Application.Olap;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>Default <see cref="IOlapStagingService"/>. Audit failures reject the request (fail closed, 503).</summary>
public sealed class OlapStagingService(
    IDuckDbOlapEngine olapEngine,
    ITableMetadataRepository metadataRepository,
    IAutherisConnectorRegistry connectorRegistry,
    ICrossDomainAccessResolver accessResolver,
    IColumnMaskingProvider maskingProvider,
    IOptions<GatewayOptions> gatewayOptions,
    IAuditWriter auditWriter,
    ILogger<OlapStagingService> logger) : IOlapStagingService
{
    public async Task<OlapStagingOutcome> ExecuteAsync(
        ClaimsPrincipal user,
        SecurityPrincipalContext caller,
        OlapQueryCommand command,
        bool isDevelopment,
        string traceId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(command);

        var isDev = isDevelopment;
        var options = gatewayOptions.Value.DuckDbOlap ?? new DuckDbOlapOptions();

        var tenantId = caller.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId.Value))
        {
            return new OlapStagingOutcome(403, "Forbidden: Missing tenant identity.");
        }

        // SEC D-4: every staged table and every query is audited BEFORE data is returned; an audit failure fails the request closed.
        var actorSid = caller.UserSid;
        var stagedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sqlHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(command.Sql)));

        async Task<bool> TryAuditAsync(string eventType, string targetTable, object details)
        {
            try
            {
                await auditWriter.RecordRequiredAsync(new AuditLogEntry
                {
                    TenantId = tenantId,
                    EventType = eventType,
                    ActorSid = actorSid,
                    TargetTable = targetTable,
                    Decision = "ALLOW",
                    TraceId = traceId,
                    DetailsJson = JsonSerializer.Serialize(details)
                }, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "OLAP audit write failed; request is rejected (fail-closed).");
                return false;
            }
        }

        static OlapStagingOutcome AuditFailure() =>
            new(503, "Audit unavailable; the analytical request was rejected.");

        var sources = new List<OlapTableSource>();

        if (command.TableNames != null && command.TableNames.Count > 0)
        {
            int maxTableCount = options.MaxTableCount > 0 ? options.MaxTableCount : 10;
            var distinctTableNames = command.TableNames
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinctTableNames.Count > maxTableCount)
            {
                logger.LogWarning("OLAP request exceeded maximum table count: {Count} > {Max}", distinctTableNames.Count, maxTableCount);
                return new OlapStagingOutcome(400, $"OLAP request exceeds maximum allowed distinct table count of {maxTableCount}.");
            }

            int totalStagedRows = 0;
            int maxTotalStagedRows = options.MaxTotalStagedRows > 0 ? options.MaxTotalStagedRows : 500000;

            foreach (var rawTableName in distinctTableNames)
            {
                TableMetadata? meta = null;
                if (TableIdentifier.TryParse(rawTableName, out var parsedId))
                {
                    meta = await metadataRepository.GetTableMetadataAsync(parsedId, ct).ConfigureAwait(false);
                }
                else
                {
                    var allTables = await metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
                    meta = allTables.FirstOrDefault(t =>
                        string.Equals(t.Identifier.TableName, rawTableName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.Identifier.ToQualifiedName(), rawTableName, StringComparison.OrdinalIgnoreCase));
                }

                if (meta == null)
                {
                    logger.LogWarning("OLAP request table '{Table}' not found in metadata catalog.", rawTableName);
                    return new OlapStagingOutcome(
                        isDev ? 404 : 403,
                        isDev ? $"Table '{rawTableName}' not found in metadata catalog." : $"Access denied to table '{rawTableName}'.");
                }

                // SR-H-06: tenant/global data-source allowlists apply before any access decision or read.
                try
                {
                    Autheris.Application.Sql.Services.DataSourceAllowlistGuard.EnsureAllowed(
                        gatewayOptions.Value.WebSql ?? new WebSqlOptions(), tenantId,
                        meta.Identifier.Domain);
                }
                catch (Autheris.Application.Sql.WebSqlPolicyException)
                {
                    logger.LogWarning("OLAP request denied: data source of table {Table} is not allowed for the tenant.", meta.Identifier);
                    return new OlapStagingOutcome(403, $"Access denied to table '{rawTableName}'.");
                }

                var effectiveUser = user;

                // SEC-OLAP-05 / RR-L3-04 / Architecture 1: one access decision (ReBAC whenever enabled, consents, Casbin, RLS)
                // without leaking internal policy details.
                var decision = await accessResolver.ResolveAccessAsync(effectiveUser, meta.Identifier, meta, tenantId, ct).ConfigureAwait(false);

                if (!decision.IsAllowed)
                {
                    logger.LogWarning("ABAC access denied for table {Table}: {Reasons}", meta.Identifier, string.Join("; ", decision.DeniedReasons));
                    return new OlapStagingOutcome(
                        403,
                        isDev ? $"Access denied to table '{meta.Identifier}': {string.Join("; ", decision.DeniedReasons)}" : $"Access denied to table '{rawTableName}'.");
                }

                bool canUseConnector = connectorRegistry.TryGetConnectorForTable(meta.Identifier, out var connector) &&
                    connector != null &&
                    (meta.Table.DataSourceType == DataSourceType.Sql ||
                     (!string.Equals(connector.ConnectorId, "default-sql", StringComparison.OrdinalIgnoreCase) &&
                      !string.Equals(connector.ConnectorId, "sql", StringComparison.OrdinalIgnoreCase)));

                if (!canUseConnector || connector == null)
                {
                    logger.LogWarning("No active connector registered for table {Table}", meta.Identifier);
                    return new OlapStagingOutcome(
                        isDev ? 502 : 403,
                        isDev ? $"No active connector registered for table '{meta.Identifier}'." : $"Access denied to table '{rawTableName}'.");
                }

                var session = new ConnectorSessionContext(
                    Principal: effectiveUser,
                    Tenant: tenantId,
                    AccessDecision: decision,
                    ProjectedColumns: meta.Columns.Select(c => c.ColumnName).ToList(),
                    Arguments: new Dictionary<string, object?> { ["limit"] = options.MaxStagedRowsPerTable + 1 },
                    PushdownFilterSql: decision.CombinedRowFilterSql,
                    Limit: options.MaxStagedRowsPerTable + 1,
                    Offset: 0);

                // Architecture 2 / SQL2-5: the governed reader applies the row filter when the connector did not push it
                // down, masks exactly once (SQL2-4) and checks the staging capacity before rows are kept (SQL2-2).
                List<IReadOnlyDictionary<string, object?>> maskedRows;
                try
                {
                    var read = await GovernedConnectorReader.ReadAsync(
                        connector,
                        session,
                        meta,
                        new GovernedRowPolicy(
                            maskingProvider,
                            gatewayOptions.Value.DataMasking?.HmacKeyId,
                            MaskingDisabled: gatewayOptions.Value.IsColumnMaskingDisabled,
                            MaxRows: options.MaxStagedRowsPerTable),
                        ct).ConfigureAwait(false);
                    maskedRows = read.Rows.ToList();
                }
                catch (ConnectorRowLimitExceededException)
                {
                    return new OlapStagingOutcome(400, $"Table '{meta.Identifier}' exceeds maximum allowed staging rows ({options.MaxStagedRowsPerTable}).");
                }

                totalStagedRows += maskedRows.Count;
                if (totalStagedRows > maxTotalStagedRows)
                {
                    logger.LogWarning("OLAP request exceeded total staged rows limit across tables: {Total} > {Max}", totalStagedRows, maxTotalStagedRows);
                    return new OlapStagingOutcome(400, $"Total staged rows across all tables ({totalStagedRows}) exceeded maximum allowed limit ({maxTotalStagedRows}).");
                }

                if (!await TryAuditAsync("OLAP_TABLE_STAGED", meta.Identifier.ToQualifiedName(), new
                {
                    tenant = tenantId.Value,
                    sid = actorSid.Value,
                    table = meta.Identifier.ToQualifiedName(),
                    rowCount = maskedRows.Count,
                    sqlSha256 = sqlHash
                }).ConfigureAwait(false))
                {
                    return AuditFailure();
                }

                stagedCounts[meta.Identifier.ToQualifiedName()] = maskedRows.Count;
                sources.Add(new OlapTableSource(meta.Identifier, maskedRows, meta));
            }
        }

        try
        {
            var queryRequest = new OlapQueryRequest(command.Sql, sources, command.Limit);
            var result = await olapEngine.ExecuteOlapQueryAsync(queryRequest, ct).ConfigureAwait(false);

            if (!await TryAuditAsync("OLAP_QUERY", string.Join(",", stagedCounts.Keys), new
            {
                tenant = tenantId.Value,
                sid = actorSid.Value,
                tables = stagedCounts,
                stagedRowCount = stagedCounts.Values.Sum(),
                resultRowCount = result.Rows.Count,
                totalRows = result.TotalRowCount,
                sqlSha256 = sqlHash
            }).ConfigureAwait(false))
            {
                return AuditFailure();
            }

            return new OlapStagingOutcome(200, Result: result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("MaxStagedRowsPerTable", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(ex, "Max staged rows limit exceeded in OLAP query.");
            return new OlapStagingOutcome(400, ex.Message);
        }
        catch (SecurityException ex) when (ex.Message.Contains("capacity", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(ex, "OLAP engine concurrency limit reached.");
            return new OlapStagingOutcome(429, "OLAP capacity limit reached. Please retry later.");
        }
        catch (SecurityException ex) when (ex.Message.Contains("memory size", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(ex, "OLAP query result byte budget exceeded.");
            return new OlapStagingOutcome(400, isDev ? ex.Message : "OLAP query result exceeded maximum allowed memory size.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing DuckDB OLAP query.");
            return isDev || gatewayOptions.Value.IsQuickstartProfile
                ? new OlapStagingOutcome(400, "Analytical query execution failed.", ex.Message)
                : new OlapStagingOutcome(400, "Analytical query execution failed.");
        }
    }
}
