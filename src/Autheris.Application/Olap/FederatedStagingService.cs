namespace Autheris.Application.Olap;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.Adapters;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class FederatedStagingService : IFederatedStagingService
{
    private readonly IAutherisConnectorRegistry _connectorRegistry;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly ILogger<FederatedStagingService> _logger;
    private readonly IEnumerable<IDataSourceExecutor>? _executors;
    private readonly ITableMetadataRepository? _metadataRepository;

    public FederatedStagingService(
        IAutherisConnectorRegistry connectorRegistry,
        IColumnMaskingProvider maskingProvider,
        IAuditLogRepository auditRepo,
        IOptions<GatewayOptions> gatewayOptions,
        ILogger<FederatedStagingService> logger,
        IEnumerable<IDataSourceExecutor>? executors = null,
        ITableMetadataRepository? metadataRepository = null)
    {
        _connectorRegistry = connectorRegistry ?? throw new ArgumentNullException(nameof(connectorRegistry));
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
        _auditRepo = auditRepo ?? throw new ArgumentNullException(nameof(auditRepo));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _executors = executors;
        _metadataRepository = metadataRepository;
    }

    public async Task<IReadOnlyList<OlapTableSource>> StageAsync(
        IReadOnlyList<StagingTableRequest> tables,
        ClaimsPrincipal user,
        TenantId tenantId,
        FederationBudget budget,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(budget);

        if (tables.Count == 0)
        {
            return Array.Empty<OlapTableSource>();
        }

        // INV-12: Enforce maximum table count
        if (tables.Count > budget.MaxTableCount)
        {
            _logger.LogWarning("Federated query exceeded maximum table count: {Count} > {Max}", tables.Count, budget.MaxTableCount);
            throw new GatewaySecurityException($"Federated query exceeds maximum allowed distinct table count of {budget.MaxTableCount}.", "LIMIT_EXCEEDED");
        }

        // INV-1 / Phase A: Authorize ALL tables BEFORE reading any data source.
        foreach (var req in tables)
        {
            if (!req.Decision.IsAllowed)
            {
                _logger.LogWarning("Federated staging denied access to table {Table}: {Reasons}",
                    req.Metadata.Identifier, string.Join("; ", req.Decision.DeniedReasons));
                throw new GatewaySecurityException($"Access denied to table '{req.Metadata.Identifier}'.", "TABLE_DENIED");
            }

            // HTTP descriptor checks (E-3, INV-8)
            if (req.Metadata.Table.DataSourceType == DataSourceType.HttpDeclarative &&
                req.Metadata.HttpEndpoint != null)
            {
                var desc = req.Metadata.HttpEndpoint;
                var isPaged = desc.Pagination != null && desc.Pagination.Strategy != HttpPaginationStrategy.None;
                if (!desc.CompleteResponse && !isPaged)
                {
                    _logger.LogWarning("Federated staging rejected HTTP table {Table}: CompleteResponse is false.", req.Metadata.Identifier);
                    throw new GatewaySecurityException($"HTTP table '{req.Metadata.Identifier}' does not declare CompleteResponse=true and cannot be federated.", "INCOMPLETE_HTTP_RESPONSE");
                }

                if (desc.AuthMode == HttpAuthMode.ForwardBearerToken)
                {
                    _logger.LogWarning("Federated staging rejected HTTP table {Table}: ForwardBearerToken is not supported.", req.Metadata.Identifier);
                    throw new GatewaySecurityException($"HTTP table '{req.Metadata.Identifier}' uses ForwardBearerToken which is not permitted in cross-source federation.", "AUTH_MODE_UNSUPPORTED");
                }
            }
        }

        // Phase B: Read and stage each source under governed conditions.
        var stagedSources = new List<OlapTableSource>(tables.Count);
        int totalStagedRows = 0;
        long totalStagedBytes = 0;
        var actorSid = user.GetUserSid()?.Value ?? "anonymous";

        foreach (var req in tables)
        {
            if (req.PreloadedRows != null)
            {
                var preloaded = req.PreloadedRows.ToList();
                totalStagedRows += preloaded.Count;
                totalStagedBytes += GovernedConnectorReader.EstimateBytes(preloaded);
                stagedSources.Add(new OlapTableSource(
                    Table: req.Metadata.Identifier,
                    GovernedRows: preloaded,
                    Metadata: req.Metadata,
                    StagingTableName: req.StagingName,
                    MaskedColumns: new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                continue;
            }

            var connector = ResolveConnector(req.Metadata);
            if (connector == null)
            {
                _logger.LogWarning("No active connector available for table {Table}", req.Metadata.Identifier);
                throw new GatewaySecurityException($"No active connector available for table '{req.Metadata.Identifier}'.", "CONNECTOR_UNAVAILABLE");
            }

            var sessionArgs = new Dictionary<string, object?> { ["limit"] = budget.MaxStagedRowsPerTable + 1 };
            if (req.CustomArguments != null)
            {
                foreach (var (k, v) in req.CustomArguments)
                {
                    sessionArgs[k] = v;
                }
            }

            var session = new ConnectorSessionContext(
                Principal: user,
                Tenant: tenantId,
                AccessDecision: req.Decision,
                ProjectedColumns: req.Projection.Count > 0 ? req.Projection : req.Metadata.Columns.Select(c => c.ColumnName).ToList(),
                Arguments: sessionArgs,
                PushdownFilterSql: req.Decision.CombinedRowFilterSql,
                Limit: budget.MaxStagedRowsPerTable + 1,
                Offset: 0);

            if (req.SessionItems != null)
            {
                foreach (var (k, v) in req.SessionItems)
                {
                    session.Items[k] = v;
                }
            }

            if (req.PushdownFilter != null && req.Metadata.Table.DataSourceType == DataSourceType.Sql)
            {
                session.Items[TableQueryItems.Filter] = req.PushdownFilter;
            }

            var rowPolicy = new GovernedRowPolicy(
                MaskingProvider: _maskingProvider,
                HmacKeyId: _gatewayOptions.Value.DataMasking?.HmacKeyId,
                MaskingDisabled: _gatewayOptions.Value.IsColumnMaskingDisabled,
                MaxRows: budget.MaxStagedRowsPerTable,
                MaxBytes: budget.MaxStagedBytesPerTable);

            GovernedReadResult readResult;
            try
            {
                readResult = await GovernedConnectorReader.ReadAsync(
                    connector,
                    session,
                    req.Metadata,
                    rowPolicy,
                    ct).ConfigureAwait(false);
            }
            catch (Autheris.Application.Connectors.ConnectorRowLimitExceededException ex)
            {
                _logger.LogWarning("Table {Table} exceeded staging limit of {Limit} rows.", ex.Table, ex.MaxRows);
                throw new GatewaySecurityException($"Table '{ex.Table}' exceeds maximum allowed staging rows ({ex.MaxRows}).", "ROW_LIMIT_EXCEEDED");
            }

            var rows = readResult.Rows;
            totalStagedRows += rows.Count;
            if (totalStagedRows > budget.MaxTotalStagedRows)
            {
                _logger.LogWarning("Federated staging exceeded total row limit: {Total} > {Max}", totalStagedRows, budget.MaxTotalStagedRows);
                throw new GatewaySecurityException($"Federated query exceeds maximum allowed total staged rows of {budget.MaxTotalStagedRows}.", "TOTAL_ROW_LIMIT_EXCEEDED");
            }

            long tableBytes = GovernedConnectorReader.EstimateBytes(rows);
            totalStagedBytes += tableBytes;
            if (totalStagedBytes > budget.MaxTotalStagedBytes)
            {
                _logger.LogWarning("Federated staging exceeded total byte limit: {Total} > {Max}", totalStagedBytes, budget.MaxTotalStagedBytes);
                throw new GatewaySecurityException($"Federated query exceeds maximum allowed total staged bytes of {budget.MaxTotalStagedBytes}.", "TOTAL_BYTES_LIMIT_EXCEEDED");
            }

            // Audit record for this source read (fail-closed, INV-17)
            try
            {
                var auditDetails = new Dictionary<string, object?>
                {
                    ["table"] = req.Metadata.Identifier.ToQualifiedName(),
                    ["stagingName"] = req.StagingName,
                    ["dataSourceType"] = req.Metadata.Table.DataSourceType.ToString(),
                    ["sourceName"] = req.Metadata.Table.SourceName ?? req.Metadata.Identifier.Domain,
                    ["rowCount"] = rows.Count,
                    ["rlsPushedDown"] = readResult.RlsPushdownExecuted,
                    ["inDbMaskingExecuted"] = readResult.InDbMaskingExecuted,
                    ["filterPushedDown"] = req.PushdownFilter != null
                };

                await _auditRepo.RecordAuditEventAsync(new AuditLogEntry
                {
                    TenantId = tenantId.Value,
                    EventType = AuditEventTypes.WebSqlCrossSourceSourceRead,
                    ActorSid = actorSid,
                    TargetTable = req.Metadata.Identifier.ToQualifiedName(),
                    Decision = "ALLOW",
                    TraceId = Guid.NewGuid().ToString("N"),
                    DetailsJson = JsonSerializer.Serialize(auditDetails)
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Federated staging audit write failed for table {Table}; failing closed.", req.Metadata.Identifier);
                throw new InvalidOperationException($"Audit logging failed for table '{req.Metadata.Identifier}'; federated staging aborted.", ex);
            }

            // Determine masked columns for DuckDB VARCHAR schema mapping
            var maskedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in req.Metadata.Columns)
            {
                var access = req.Decision.GetColumnAccess(col.ColumnName);
                if (access == ColumnAccessLevel.Mask ||
                    (req.Metadata.ColumnMaskingRules != null && req.Metadata.ColumnMaskingRules.ContainsKey(col.ColumnName) && access != ColumnAccessLevel.Clear))
                {
                    maskedColumns.Add(col.ColumnName);
                }
            }

            stagedSources.Add(new OlapTableSource(
                Table: req.Metadata.Identifier,
                GovernedRows: rows,
                Metadata: req.Metadata,
                StagingTableName: req.StagingName,
                MaskedColumns: maskedColumns));
        }

        return stagedSources;
    }

    private IAutherisConnector? ResolveConnector(TableMetadata metadata)
    {
        if (metadata.Table.DataSourceType == DataSourceType.Sql)
        {
            if (_connectorRegistry.TryGetConnectorForTable(metadata.Identifier, out var sqlConn) && sqlConn != null)
            {
                return sqlConn;
            }
            return null;
        }

        if (metadata.Table.DataSourceType == DataSourceType.HttpDeclarative)
        {
            // Do not fall back to default-sql or sql!
            if (_connectorRegistry.TryGetConnectorForTable(metadata.Identifier, out var conn) && conn != null)
            {
                if (!string.Equals(conn.ConnectorId, "default-sql", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(conn.ConnectorId, "sql", StringComparison.OrdinalIgnoreCase))
                {
                    return conn;
                }
            }

            // If an IDataSourceExecutor exists for HttpDeclarative, adapt it
            if (_executors != null)
            {
                var httpExec = _executors.FirstOrDefault(e => e.SupportedType == DataSourceType.HttpDeclarative);
                if (httpExec != null)
                {
                    return new LegacyDataSourceExecutorAdapter(httpExec, "http-declarative-adapter", _metadataRepository);
                }
            }

            return null;
        }

        return null;
    }
}
