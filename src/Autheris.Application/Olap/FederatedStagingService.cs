namespace Autheris.Application.Olap;

using Autheris.Application.Common;
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
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Application.Sql.Federation.KeySet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine.Ast.Emit;

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

            // SR-H-06: tenant/global data-source allowlists apply to every staged table before any read.
            // SQLA-07: both the identifier domain and the effective source (catalog SourceName, mapped) must pass; the connection below uses the latter.
            var webSqlOptions = _gatewayOptions.Value.WebSql ?? new WebSqlOptions();
            Autheris.Application.Sql.Services.DataSourceAllowlistGuard.EnsureAllowed(
                webSqlOptions, tenantId,
                req.Metadata.Identifier.Domain);
            Autheris.Application.Sql.Services.DataSourceAllowlistGuard.EnsureAllowed(
                webSqlOptions, tenantId,
                Autheris.Application.Sql.Services.DataSourceAllowlistGuard.EffectiveDataSource(webSqlOptions, req.Metadata));

            if (req.Metadata.RequiredFilterColumns.Length > 0 || req.Metadata.RequiredFilterColumnGroups.Length > 0)
            {
                bool IsSatisfied(string reqCol)
                {
                    if (req.Metadata.TenantColumnName != null &&
                        string.Equals(req.Metadata.TenantColumnName, reqCol, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (req.Decision.RowPolicy is Autheris.Application.Policy.PolicyPredicateRowPolicy rls &&
                        rls.Predicate.ReferencedColumns.Contains(reqCol.ToLowerInvariant()))
                    {
                        return true;
                    }

                    if (!string.IsNullOrWhiteSpace(req.Decision.CombinedRowFilterSql) &&
                        req.Decision.CombinedRowFilterSql.Contains(reqCol, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (req.PushdownFilter != null &&
                        req.PushdownFilter.ReferencedColumns.Any(c => string.Equals(c, reqCol, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }

                    return false;
                }

                if (req.Metadata.RequiredFilterColumnGroups.Length > 0)
                {
                    bool anyGroupSatisfied = false;
                    foreach (var group in req.Metadata.RequiredFilterColumnGroups)
                    {
                        if (group.Length == 0) continue;
                        if (group.All(IsSatisfied))
                        {
                            anyGroupSatisfied = true;
                            break;
                        }
                    }

                    if (!anyGroupSatisfied)
                    {
                        throw new GatewaySecurityException(
                            GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.RequiredFilterColumnMissing),
                            GovernedSqlErrorCodes.RequiredFilterColumnMissing);
                    }
                }

                if (req.Metadata.RequiredFilterColumns.Length > 0)
                {
                    foreach (var reqCol in req.Metadata.RequiredFilterColumns)
                    {
                        if (!IsSatisfied(reqCol))
                        {
                            throw new GatewaySecurityException(
                                GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.RequiredFilterColumnMissing),
                                GovernedSqlErrorCodes.RequiredFilterColumnMissing);
                        }
                    }
                }
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

                // ForwardBearerToken is never federated (stable code), even if an operator lists it in the allowlist.
                if (desc.AuthMode == HttpAuthMode.ForwardBearerToken)
                {
                    _logger.LogWarning("Federated staging rejected HTTP table {Table}: ForwardBearerToken is not supported.", req.Metadata.Identifier);
                    throw new GatewaySecurityException($"HTTP table '{req.Metadata.Identifier}' uses ForwardBearerToken which is not permitted in cross-source federation.", "AUTH_MODE_UNSUPPORTED");
                }

                // CrossSource.AllowedHttpAuthModes (fail-closed): the mode must be listed (default: None, StaticApiKey, ClientCredentials).
                var allowedModes = (_gatewayOptions.Value.WebSql?.CrossSource ?? new CrossSourceOptions()).GetEffectiveAllowedHttpAuthModes();
                if (!allowedModes.Contains(desc.AuthMode.ToString(), StringComparer.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Federated staging rejected HTTP table {Table}: auth mode {AuthMode} is not in CrossSource:AllowedHttpAuthModes.", req.Metadata.Identifier, desc.AuthMode);
                    throw new GatewaySecurityException($"HTTP table '{req.Metadata.Identifier}' uses an authentication mode that is not permitted in cross-source federation.", "HTTP_AUTH_MODE_DENIED");
                }
            }
        }

        // Phase B: Read and stage each source under governed conditions.
        var orderedTables = tables;
        if (tables.Any(t => t.KeySetPlan != null))
        {
            var drivingStagingNames = new HashSet<string>(
                tables.Where(t => t.KeySetPlan != null).Select(t => t.KeySetPlan!.DrivingStagingName),
                StringComparer.OrdinalIgnoreCase);

            var sorted = tables.ToList();
            sorted.Sort((a, b) =>
            {
                bool aIsDriving = drivingStagingNames.Contains(a.StagingName);
                bool bIsDriving = drivingStagingNames.Contains(b.StagingName);
                if (aIsDriving && !bIsDriving) return -1;
                if (!aIsDriving && bIsDriving) return 1;
                return 0;
            });
            orderedTables = sorted;
        }

        var stagedSources = new List<OlapTableSource>(tables.Count);
        var stagedByStagingName = new Dictionary<string, OlapTableSource>(StringComparer.OrdinalIgnoreCase);
        var requestByStagingName = tables.ToDictionary(r => r.StagingName, StringComparer.OrdinalIgnoreCase);
        int totalStagedRows = 0;
        long totalStagedBytes = 0;
        var actorSid = user.GetUserSid()?.Value ?? "anonymous";

        foreach (var req in orderedTables)
        {
            if (req.Subtree != null)
            {
                var subtree = req.Subtree;
                var subtreeConnector = ResolveConnector(subtree.MemberTables[0].Metadata);
                if (subtreeConnector == null)
                {
                    _logger.LogWarning("No active connector available for subtree {Subtree}", subtree.SubtreeId);
                    throw new GatewaySecurityException($"No active connector available for table '{subtree.MemberTables[0].Metadata.Identifier}'.", "CONNECTOR_UNAVAILABLE");
                }

                var subtreeSessionArgs = new Dictionary<string, object?> { ["limit"] = budget.MaxStagedRowsPerTable + 1 };
                var subtreeSession = new ConnectorSessionContext(
                    Principal: user,
                    Tenant: tenantId,
                    AccessDecision: req.Decision,
                    ProjectedColumns: subtree.ProjectedColumns.Count > 0 ? subtree.ProjectedColumns : subtree.SyntheticMetadata.Columns.Select(c => c.ColumnName).ToList(),
                    Arguments: subtreeSessionArgs,
                    PushdownFilterSql: subtree.Compiled.Sql,
                    Limit: budget.MaxStagedRowsPerTable + 1,
                    Offset: 0);

                subtreeSession.Items["TableMetadata"] = subtree.SyntheticMetadata;
                subtreeSession.Items["Subtree"] = subtree;
                subtreeSession.Items["SubtreePlan"] = subtree;
                subtreeSession.Items["SubtreeSql"] = subtree.Compiled.Sql;
                if (subtree.Parameters != null)
                {
                    subtreeSession.Items["SubtreeParameters"] = subtree.Parameters;
                }

                var subtreeRowPolicy = new GovernedRowPolicy(
                    MaskingProvider: _maskingProvider,
                    HmacKeyId: _gatewayOptions.Value.DataMasking?.HmacKeyId,
                    MaskingDisabled: _gatewayOptions.Value.IsColumnMaskingDisabled,
                    MaxRows: budget.MaxStagedRowsPerTable,
                    MaxBytes: budget.MaxStagedBytesPerTable);

                var clock = System.Diagnostics.Stopwatch.StartNew();
                GovernedReadResult subtreeReadResult;
                try
                {
                    subtreeReadResult = await GovernedConnectorReader.ReadAsync(
                        subtreeConnector,
                        subtreeSession,
                        subtree.SyntheticMetadata,
                        subtreeRowPolicy,
                        ct).ConfigureAwait(false);
                }
                catch (Autheris.Application.Connectors.ConnectorRowLimitExceededException ex)
                {
                    _logger.LogWarning("Subtree {Subtree} exceeded staging limit of {Limit} rows.", subtree.SubtreeId, ex.MaxRows);
                    throw new GatewaySecurityException($"Subtree '{subtree.SubtreeId}' exceeds maximum allowed staging rows ({ex.MaxRows}).", "ROW_LIMIT_EXCEEDED");
                }
                clock.Stop();

                var subtreeRows = subtreeReadResult.Rows;
                totalStagedRows += subtreeRows.Count;
                if (totalStagedRows > budget.MaxTotalStagedRows)
                {
                    _logger.LogWarning("Federated staging exceeded total row limit: {Total} > {Max}", totalStagedRows, budget.MaxTotalStagedRows);
                    throw new GatewaySecurityException($"Federated query exceeds maximum allowed total staged rows of {budget.MaxTotalStagedRows}.", "TOTAL_ROW_LIMIT_EXCEEDED");
                }

                long subtreeTableBytes = GovernedConnectorReader.EstimateBytes(subtreeRows);
                totalStagedBytes += subtreeTableBytes;
                if (totalStagedBytes > budget.MaxTotalStagedBytes)
                {
                    _logger.LogWarning("Federated staging exceeded total byte limit: {Total} > {Max}", totalStagedBytes, budget.MaxTotalStagedBytes);
                    throw new GatewaySecurityException($"Federated query exceeds maximum allowed total staged bytes of {budget.MaxTotalStagedBytes}.", "TOTAL_BYTES_LIMIT_EXCEEDED");
                }

                // WP-123-4: Dual-Tier Audit Logging (SEC-DATA123-05)
                try
                {
                    var subtreeAuditDetails = new Dictionary<string, object?>
                    {
                        ["subtreeId"] = subtree.SubtreeId,
                        ["sourceName"] = subtree.SourceName,
                        ["stagingName"] = req.StagingName,
                        ["dialect"] = subtree.Dialect.ToString(),
                        ["memberTables"] = subtree.MemberTables.Select(m => m.Metadata.Identifier.ToString()).ToList(),
                        ["rowCount"] = subtreeRows.Count,
                        ["stagedRowCount"] = subtreeRows.Count,
                        ["stagedBytes"] = subtreeTableBytes,
                        ["executionMs"] = clock.ElapsedMilliseconds
                    };

                    await _auditRepo.RecordAuditEventAsync(new AuditLogEntry
                    {
                        TenantId = tenantId.Value,
                        EventType = AuditEventTypes.WebSqlCrossSourceSubtreeRead,
                        ActorSid = actorSid,
                        TargetTable = string.Join(",", subtree.MemberTables.Select(m => m.Metadata.Identifier.ToString())),
                        Decision = "ALLOW",
                        TraceId = budget.AuditCorrelationId ?? Guid.NewGuid().ToString("N"),
                        DetailsJson = JsonSerializer.Serialize(subtreeAuditDetails)
                    }, ct).ConfigureAwait(false);

                    foreach (var member in subtree.MemberTables)
                    {
                        var memberAuditDetails = new Dictionary<string, object?>
                        {
                            ["table"] = member.Metadata.Identifier.ToString(),
                            ["stagingName"] = req.StagingName,
                            ["dataSourceType"] = member.Metadata.Table.DataSourceType.ToString(),
                            ["sourceName"] = subtree.SourceName,
                            ["subtreeId"] = subtree.SubtreeId,
                            ["rowCount"] = subtreeRows.Count,
                            ["rlsPushedDown"] = true,
                            ["inDbMaskingExecuted"] = true
                        };

                        await _auditRepo.RecordAuditEventAsync(new AuditLogEntry
                        {
                            TenantId = tenantId.Value,
                            EventType = AuditEventTypes.WebSqlCrossSourceSourceRead,
                            ActorSid = actorSid,
                            TargetTable = member.Metadata.Identifier.ToString(),
                            Decision = "ALLOW",
                            TraceId = budget.AuditCorrelationId ?? Guid.NewGuid().ToString("N"),
                            DetailsJson = JsonSerializer.Serialize(memberAuditDetails)
                        }, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Error(ex, "Federated staging audit write failed for subtree {Subtree}; failing closed.", subtree.SubtreeId);
                    throw new InvalidOperationException($"Audit logging failed for subtree '{subtree.SubtreeId}'; federated staging aborted.", ex);
                }

                stagedSources.Add(new OlapTableSource(
                    Table: subtree.SyntheticMetadata.Identifier,
                    GovernedRows: subtreeRows,
                    Metadata: subtree.SyntheticMetadata,
                    StagingTableName: req.StagingName,
                    MaskedColumns: subtree.MaskedColumns));

                continue;
            }

            var connector = ResolveConnector(req.Metadata);
            if (connector == null)
            {
                _logger.LogWarning("No active connector available for table {Table}", req.Metadata.Identifier);
                throw new GatewaySecurityException($"No active connector available for table '{req.Metadata.Identifier}'.", "CONNECTOR_UNAVAILABLE");
            }

            var sessionArgs = new Dictionary<string, object?> { ["limit"] = budget.MaxStagedRowsPerTable + 1 };
            // F-GOV-14 (AC-8.5): the pushdown restricts what is fetched; it can never overwrite a gateway argument. A path
            // lookup does not pass its key list to the connector (the executor would fan out and concatenate the answers);
            // the lookup reader below sends one request per key and restricts and de-duplicates the answers (CR-01).
            var vfPlan = req.VirtualFilterPlan;
            var pathLookup = vfPlan is { SkipUpstream: false } ? vfPlan.PathLookup : null;
            if (pathLookup != null)
            {
                EnsureNoArgumentConflict(sessionArgs, pathLookup.Argument, req.Metadata);
            }
            else if (vfPlan?.PushdownArguments is { } pushdown)
            {
                foreach (var (name, value) in pushdown)
                {
                    EnsureNoArgumentConflict(sessionArgs, name, req.Metadata);
                    sessionArgs[name] = value;
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

            if (req.PushdownFilter != null && req.Metadata.Table.DataSourceType == DataSourceType.Sql)
            {
                session.Items[TableQueryItems.Filter] = req.PushdownFilter;
            }

            var rowPolicy = new GovernedRowPolicy(
                MaskingProvider: _maskingProvider,
                HmacKeyId: _gatewayOptions.Value.DataMasking?.HmacKeyId,
                MaskingDisabled: _gatewayOptions.Value.IsColumnMaskingDisabled,
                MaxRows: budget.MaxStagedRowsPerTable,
                MaxBytes: budget.MaxStagedBytesPerTable,
                // F-GOV-14 (C-6): the enforcement travels as a typed parameter, never through session.Items. A skipped
                // upstream read (Tier 0) runs after the guards above and the connector resolution (SEC-13).
                VirtualFilterEnforcement: req.VirtualFilterPlan?.Enforcement,
                SkipUpstream: req.VirtualFilterPlan?.SkipUpstream == true);

            var readConnector = connector;
            PathLookupConnector? lookupConnector = null;
            if (pathLookup != null)
            {
                lookupConnector = new PathLookupConnector(connector, pathLookup, req.Metadata.HttpEndpoint?.MaxConcurrentRequests ?? 1);
                readConnector = lookupConnector;
            }

            bool isKeySetCandidate = req.KeySetPlan != null &&
                                     req.Metadata.Table.DataSourceType == DataSourceType.Sql &&
                                     (_gatewayOptions.Value.WebSql?.CrossSource?.DynamicKeySet?.Enabled ?? true);

            OlapTableSource? drivingSource = null;
            TableAccessDecision? drivingDecision = null;
            bool keySetAllowed = false;

            if (isKeySetCandidate)
            {
                stagedByStagingName.TryGetValue(req.KeySetPlan!.DrivingStagingName, out drivingSource);
                if (requestByStagingName.TryGetValue(req.KeySetPlan.DrivingStagingName, out var drivingReq))
                {
                    drivingDecision = drivingReq.Decision;
                }

                var targetSourceName = req.Metadata.Table.SourceName ?? req.Metadata.Identifier.Domain;
                var connections = _gatewayOptions.Value.DataSources?.Connections;
                DataSourceConnectionOptions? targetConn = null;
                if (connections != null)
                {
                    string effectiveSourceName = targetSourceName;
                    if (_gatewayOptions.Value.WebSql?.DataSourceMappings != null &&
                        _gatewayOptions.Value.WebSql.DataSourceMappings.TryGetValue(targetSourceName, out var mapped) &&
                        !string.IsNullOrWhiteSpace(mapped))
                    {
                        effectiveSourceName = mapped;
                    }
                    connections.TryGetValue(effectiveSourceName, out targetConn);
                }

                keySetAllowed = drivingSource != null &&
                                drivingDecision != null &&
                                KeySetGovernanceGuard.CanApplyKeySetPushdown(
                                    drivingSource.Metadata,
                                    drivingDecision,
                                    req.KeySetPlan.DrivingKeyColumn,
                                    req.Metadata,
                                    req.Decision,
                                    req.KeySetPlan.DrivenKeyColumn,
                                    targetConn);
            }

            IReadOnlyList<object> distinctKeys = Array.Empty<object>();
            bool keySetApplied = false;
            int batchesExecuted = 0;
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;
            bool rlsPushedDown = false;
            bool inDbMaskingExecuted = false;
            int? rowsBeforeVf = null;

            if (keySetAllowed && drivingSource != null)
            {
                distinctKeys = KeyBatcher.ExtractDistinctKeys(drivingSource.GovernedRows, req.KeySetPlan!.DrivingKeyColumn);
                var keySetOpts = _gatewayOptions.Value.WebSql?.CrossSource?.DynamicKeySet ?? new DynamicKeySetOptions();
                int batchSize = KeyBatcher.ResolveBatchSize(req.Metadata.Dialect, keySetOpts.MaxKeySetBatchSize);
                int batchCount = distinctKeys.Count == 0 ? 1 : (distinctKeys.Count + batchSize - 1) / batchSize;

                if (KeyBatcher.IsWithinBounds(distinctKeys.Count, batchCount, keySetOpts.MaxKeySetKeys, keySetOpts.MaxKeySetBatches))
                {
                    keySetApplied = true;
                    var batches = distinctKeys.Count == 0 ? [Array.Empty<object>()] : KeyBatcher.CreateBatches(distinctKeys, batchSize);
                    var combinedRows = new List<IReadOnlyDictionary<string, object?>>();
                    var seenRowKeys = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var batch in batches)
                    {
                        var batchFilter = KeySetClauseBuilder.Build(req.KeySetPlan.DrivenKeyColumn, batch, req.PushdownFilter);
                        var batchSessionArgs = new Dictionary<string, object?>(sessionArgs);
                        var batchSession = new ConnectorSessionContext(
                            Principal: user,
                            Tenant: tenantId,
                            AccessDecision: req.Decision,
                            ProjectedColumns: req.Projection.Count > 0 ? req.Projection : req.Metadata.Columns.Select(c => c.ColumnName).ToList(),
                            Arguments: batchSessionArgs,
                            PushdownFilterSql: req.Decision.CombinedRowFilterSql,
                            Limit: budget.MaxStagedRowsPerTable + 1,
                            Offset: 0);
                        batchSession.Items[TableQueryItems.Filter] = batchFilter;

                        GovernedReadResult batchResult;
                        try
                        {
                            batchResult = await GovernedConnectorReader.ReadAsync(
                                readConnector,
                                batchSession,
                                req.Metadata,
                                rowPolicy,
                                ct).ConfigureAwait(false);
                        }
                        catch (Autheris.Application.Connectors.ConnectorRowLimitExceededException ex)
                        {
                            _logger.LogWarning("Table {Table} exceeded staging limit of {Limit} rows.", ex.Table, ex.MaxRows);
                            throw new GatewaySecurityException($"Table '{ex.Table}' exceeds maximum allowed staging rows ({ex.MaxRows}).", "ROW_LIMIT_EXCEEDED");
                        }

                        batchesExecuted++;
                        if (batchResult.RlsPushdownExecuted) rlsPushedDown = true;
                        if (batchResult.InDbMaskingExecuted) inDbMaskingExecuted = true;

                        // SEC-DATA124-04: Gateway Ordinal Deduplication
                        foreach (var row in batchResult.Rows)
                        {
                            var fingerprint = ComputeRowFingerprint(row, req.Metadata.PrimaryKeyColumns);
                            if (seenRowKeys.Add(fingerprint))
                            {
                                combinedRows.Add(row);
                                if (combinedRows.Count > budget.MaxStagedRowsPerTable)
                                {
                                    throw new GatewaySecurityException($"Table '{req.Metadata.Identifier}' exceeds maximum allowed staging rows ({budget.MaxStagedRowsPerTable}).", "ROW_LIMIT_EXCEEDED");
                                }
                            }
                        }
                    }

                    rows = combinedRows;
                }
                else
                {
                    _logger.LogInformation("Dynamic key-set pushdown bounds exceeded for {Table} (keys: {KeyCount}, batches: {BatchCount}). Falling back to plain staging.",
                        req.Metadata.Identifier, distinctKeys.Count, batchCount);
                    keySetApplied = false;
                    rows = null!;
                }
            }
            else
            {
                rows = null!;
            }

            if (!keySetApplied)
            {
                GovernedReadResult readResult;
                try
                {
                    readResult = await GovernedConnectorReader.ReadAsync(
                        readConnector,
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

                rows = readResult.Rows;
                rlsPushedDown = readResult.RlsPushdownExecuted;
                inDbMaskingExecuted = readResult.InDbMaskingExecuted;
                rowsBeforeVf = readResult.RowsBeforeVirtualFilter;
            }

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

            // Audit record for this source read (fail-closed, INV-17, SEC-DATA124-05)
            try
            {
                var auditDetails = new Dictionary<string, object?>
                {
                    ["table"] = req.Metadata.Identifier.ToString(),   // with the domain: two sources may share schema.table (SEC-1)
                    ["stagingName"] = req.StagingName,
                    ["dataSourceType"] = req.Metadata.Table.DataSourceType.ToString(),
                    ["sourceName"] = req.Metadata.Table.SourceName ?? req.Metadata.Identifier.Domain,
                    ["rowCount"] = rows.Count,
                    ["rlsPushedDown"] = rlsPushedDown,
                    ["inDbMaskingExecuted"] = inDbMaskingExecuted,
                    ["filterPushedDown"] = req.PushdownFilter != null || keySetApplied,
                    ["pushedPredicateCount"] = req.PushdownFilter?.PushdownPredicates?.Count ?? (req.PushdownFilter != null || keySetApplied ? 1 : 0),
                    ["pushedPredicates"] = req.PushdownFilter?.PushdownPredicates != null
                        ? req.PushdownFilter.PushdownPredicates.Select(p => new Dictionary<string, string> { ["column"] = p.Column, ["operator"] = p.Operator }).ToList()
                        : (object)Array.Empty<Dictionary<string, string>>(),
                    ["parameterNames"] = req.ParameterAudit?.ParameterNames ?? (object)Array.Empty<string>(),
                    ["listParameterCounts"] = req.ParameterAudit?.ListParameterCounts ?? (object)new Dictionary<string, int>(),
                    ["keySetFilter"] = keySetApplied,
                    ["keyCount"] = keySetApplied ? distinctKeys.Count : 0,
                    ["batchCount"] = keySetApplied ? batchesExecuted : 0,
                    ["targetColumn"] = keySetApplied ? req.KeySetPlan!.DrivenKeyColumn : null
                };
                if (vfPlan != null)
                {
                    // F-GOV-14 (FR-9): which tier read the table and how many rows the gateway enforcement removed (no key values).
                    auditDetails["vfTier"] = vfPlan.Tier.ToString();
                    auditDetails["upstreamCalled"] = !vfPlan.SkipUpstream;
                    auditDetails["pushdownKeyCount"] = vfPlan.PushdownArguments?.Values.OfType<System.Collections.ICollection>().Sum(c => c.Count) ?? 0;
                    auditDetails["rowsReceived"] = rowsBeforeVf ?? 0;
                    auditDetails["rowsKept"] = rows.Count;
                    if (lookupConnector != null)
                    {
                        // CR-01: the upstream was asked once per key; a 404 means "key not present" and is recorded here.
                        auditDetails["upstreamRequests"] = lookupConnector.Stats.Requests;
                        auditDetails["upstreamKeysNotFound"] = lookupConnector.Stats.KeysNotFound;
                    }
                    else if (vfPlan.SkipUpstream)
                    {
                        auditDetails["upstreamRequests"] = 0;
                    }
                    else if (req.Metadata.HttpEndpoint is { Pagination: null or { Strategy: HttpPaginationStrategy.None } })
                    {
                        // CR-05: one request. A paged read issues one request per page and is not counted here.
                        auditDetails["upstreamRequests"] = 1;
                    }
                }

                await _auditRepo.RecordAuditEventAsync(new AuditLogEntry
                {
                    TenantId = tenantId.Value,
                    EventType = AuditEventTypes.WebSqlCrossSourceSourceRead,
                    ActorSid = actorSid,
                    TargetTable = req.Metadata.Identifier.ToString(),
                    Decision = "ALLOW",
                    TraceId = budget.AuditCorrelationId ?? Guid.NewGuid().ToString("N"),
                    DetailsJson = JsonSerializer.Serialize(auditDetails)
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, "Federated staging audit write failed for table {Table}; failing closed.", req.Metadata.Identifier);
                throw new InvalidOperationException($"Audit logging failed for table '{req.Metadata.Identifier}'; federated staging aborted.", ex);
            }

            // Determine masked columns for DuckDB VARCHAR schema mapping
            var maskedColumns = MaskedColumnSet.For(req.Metadata, req.Decision);

            var stagedSource = new OlapTableSource(
                Table: req.Metadata.Identifier,
                GovernedRows: rows,
                Metadata: req.Metadata,
                StagingTableName: req.StagingName,
                MaskedColumns: maskedColumns);

            stagedSources.Add(stagedSource);
            stagedByStagingName[req.StagingName] = stagedSource;
        }

        // Issue #132 (PLAN-DATA-132, WP-132-4, SEC-DATA132-07): Dual-tier co-located statement audit records
        var connectionMap = _gatewayOptions.Value.DataSources?.Connections;
        if (connectionMap != null)
        {
            var coLocatedByGroup = tables
                .Where(t =>
                {
                    var src = t.Metadata.Table.SourceName ?? t.Metadata.Identifier.Domain;
                    return connectionMap.TryGetValue(src, out var conn) &&
                           !string.IsNullOrWhiteSpace(conn?.CoLocationGroup) &&
                           string.Equals(conn.Provider, "SqlServer", StringComparison.OrdinalIgnoreCase);
                })
                .GroupBy(t =>
                {
                    var src = t.Metadata.Table.SourceName ?? t.Metadata.Identifier.Domain;
                    return connectionMap[src].CoLocationGroup!.Trim();
                }, StringComparer.OrdinalIgnoreCase);

            foreach (var grp in coLocatedByGroup)
            {
                var memberList = grp.ToList();
                if (memberList.Count >= 2)
                {
                    try
                    {
                        var dbs = memberList.Select(m => m.Metadata.Table.PhysicalDatabaseName ?? m.Metadata.Identifier.Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        var tableNames = memberList.Select(m => m.Metadata.Identifier.ToString()).ToList();
                        var subtreeAuditDetails = new Dictionary<string, object?>
                        {
                            ["colocated"] = true,
                            ["coLocationGroup"] = grp.Key,
                            ["databases"] = dbs,
                            ["tables"] = tableNames,
                            ["rowCount"] = totalStagedRows
                        };

                        await _auditRepo.RecordAuditEventAsync(new AuditLogEntry
                        {
                            TenantId = tenantId.Value,
                            EventType = AuditEventTypes.WebSqlCrossSourceSubtreeRead,
                            ActorSid = actorSid,
                            TargetTable = string.Join(",", tableNames),
                            Decision = "ALLOW",
                            TraceId = budget.AuditCorrelationId ?? Guid.NewGuid().ToString("N"),
                            DetailsJson = JsonSerializer.Serialize(subtreeAuditDetails)
                        }, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Error(ex, "Federated staging subtree audit write failed for group {Group}; failing closed.", grp.Key);
                        throw new InvalidOperationException($"Subtree audit logging failed for group '{grp.Key}'; federated staging aborted.", ex);
                    }
                }
            }
        }

        return stagedSources;
    }

    private static string ComputeRowFingerprint(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<string> primaryKeyColumns)
    {
        if (primaryKeyColumns.Count > 0 && primaryKeyColumns.All(row.ContainsKey))
        {
            return string.Join("\0", primaryKeyColumns.Select(c => $"{c}:{row[c]}"));
        }

        return string.Join("\0", row.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}:{kv.Value}"));
    }

    private void EnsureNoArgumentConflict(Dictionary<string, object?> sessionArgs, string name, TableMetadata metadata)
    {
        if (sessionArgs.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Federated staging denied table {Table}: the pushdown argument collides with a gateway argument.", metadata.Identifier);
            throw new GatewaySecurityException(
                $"The Virtual Filter pushdown of '{metadata.Identifier}' conflicts with a gateway argument.",
                Autheris.Application.VirtualFilters.Federation.VirtualFilterErrorCodes.PushdownArgumentConflict);
        }
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
