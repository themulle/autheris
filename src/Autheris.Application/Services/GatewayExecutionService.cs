using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Services;

public sealed partial class GatewayExecutionService : IGatewayExecutionService, ITableAccessResolver
{
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IConsentRepository _consentRepository;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IConsentResolutionService _resolutionService;
    private readonly IConsentCacheService _cacheService;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly IChunkedQueryExecutor _chunkedQueryExecutor;
    private readonly GatewayOptions? _options;
    private readonly ITrafficDrainController? _drainController;
    private readonly IEnumerable<IDataSourceExecutor>? _dataSourceExecutors;
    private readonly IPolicyEnforcementService? _policyEnforcementService;
    private readonly Autheris.Application.Security.Rebac.Interfaces.IRebacEvaluator? _rebacEvaluator;
    private readonly IClientIpResolver? _clientIpResolver;
    private readonly Autheris.Application.Connectors.IAutherisConnectorRegistry? _connectorRegistry;
    private readonly ITableReadConcurrencyGate? _concurrencyGate;
    private readonly BatchRelationLoader _batchLoader;

    /// <summary>O10: Retry-After for a throttled read; the typical duration of a slow read is the query timeout.</summary>
    private const int ThrottledRetryAfterSeconds = 2;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex EchoableColumnNameRegex();

    public int LastDispatchedChildQueryCount => _batchLoader.LastDispatchedChildQueryCount;

    [ActivatorUtilitiesConstructor]
    public GatewayExecutionService(
        ITableMetadataRepository metadataRepository,
        IConsentRepository consentRepository,
        IAuditLogRepository auditLogRepository,
        IConsentResolutionService resolutionService,
        IConsentCacheService cacheService,
        IColumnMaskingProvider maskingProvider,
        IChunkedQueryExecutor? chunkedQueryExecutor = null,
        IOptions<GatewayOptions>? options = null,
        ITrafficDrainController? drainController = null,
        IEnumerable<IDataSourceExecutor>? dataSourceExecutors = null,
        IPolicyEnforcementService? policyEnforcementService = null,
        IClientIpResolver? clientIpResolver = null,
        Autheris.Application.Connectors.IAutherisConnectorRegistry? connectorRegistry = null,
        ITableReadConcurrencyGate? concurrencyGate = null,
        Autheris.Application.Security.Rebac.Interfaces.IRebacEvaluator? rebacEvaluator = null,
        Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver? mandatoryFilters = null,
        Autheris.Application.Governance.Contracts.ISchemaContractManager? contractManager = null,
        IAccessProfileRepository? accessProfileRepository = null,
        Microsoft.Extensions.Caching.Memory.IMemoryCache? memoryCache = null)
    {
        _accessProfileRepository = accessProfileRepository;
        _memoryCache = memoryCache;
        _contractManager = contractManager;
        _mandatoryFilters = mandatoryFilters;
        _metadataRepository = metadataRepository;
        _consentRepository = consentRepository;
        _auditLogRepository = auditLogRepository;
        _resolutionService = resolutionService;
        _cacheService = cacheService;
        _maskingProvider = maskingProvider;
        _chunkedQueryExecutor = chunkedQueryExecutor ?? new ChunkedQueryExecutor(500);
        _options = options?.Value;
        _drainController = drainController;
        _dataSourceExecutors = dataSourceExecutors ?? new IDataSourceExecutor[] { new SqlDataSourceExecutor(options: options, maskingProvider: maskingProvider) };
        _policyEnforcementService = policyEnforcementService;
        _clientIpResolver = clientIpResolver;
        _connectorRegistry = connectorRegistry;
        _concurrencyGate = concurrencyGate;
        _rebacEvaluator = rebacEvaluator;
        _batchLoader = new BatchRelationLoader(_metadataRepository, _dataSourceExecutors, _maskingProvider, _chunkedQueryExecutor, _drainController, _options, CheckTableAccessAsync);
    }

    public GatewayExecutionService(
        IGovernanceRepository repository,
        IConsentResolutionService resolutionService,
        IConsentCacheService cacheService,
        IColumnMaskingProvider maskingProvider,
        IChunkedQueryExecutor? chunkedQueryExecutor = null,
        IOptions<GatewayOptions>? options = null,
        ITrafficDrainController? drainController = null,
        IEnumerable<IDataSourceExecutor>? dataSourceExecutors = null,
        IPolicyEnforcementService? policyEnforcementService = null)
        : this(repository, repository, repository, resolutionService, cacheService, maskingProvider, chunkedQueryExecutor, options, drainController, dataSourceExecutors, policyEnforcementService)
    {
    }

    public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        int? first = null,
        int? after = null,
        CancellationToken ct = default)
        => ExecuteTableQueryAsync(principal, table, first, after, null, null, null, ct);

    public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        int? first,
        int? after,
        IReadOnlyDictionary<string, object?>? queryArguments,
        IReadOnlyList<string>? requestedFields = null,
        CancellationToken ct = default)
        => ExecuteTableQueryAsync(principal, table, first, after, queryArguments, requestedFields, null, ct);

    public async Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        int? first,
        int? after,
        IReadOnlyDictionary<string, object?>? queryArguments,
        IReadOnlyList<string>? requestedFields,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        CancellationToken ct = default)
    {
        var page = await ExecutePageCoreAsync(principal, table, first, after, queryArguments, requestedFields, requestHeaders, orderBy: null, filter: null, countTotal: false, ct)
            .ConfigureAwait(false);
        return (page.Rows, page.Decision);
    }

    public Task<TableQueryPage> ExecuteTablePageAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        TablePageRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecutePageCoreAsync(
            principal, table, request.First, request.After, queryArguments: null, request.RequestedFields, request.RequestHeaders,
            request.OrderBy, request.Filter, request.IncludeTotalCount, ct);
    }

    private async Task<TableQueryPage> ExecutePageCoreAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        int? first,
        int? after,
        IReadOnlyDictionary<string, object?>? queryArguments,
        IReadOnlyList<string>? requestedFields,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        IReadOnlyList<TableOrderBy>? orderBy,
        TableFilterClause? filter,
        bool countTotal,
        CancellationToken ct)
    {
        using var _ = _drainController?.TrackQuery();
        var resolved = await ResolveTableAccessAsync(principal, table, requestedFields, requestHeaders, ct);
        principal = resolved.Principal;
        var metadata = resolved.Metadata;
        var decision = resolved.Decision;
        var tenantId = resolved.Tenant;
        var userSid = resolved.UserSid;

        // Audit evaluation (F-OPS-02: W3C Trace Correlation)
        var traceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId();

        // Enforce Access
        if (!decision.IsAllowed)
        {
            await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = "TABLE_QUERY",
                ActorSid = userSid,
                TargetTable = table.ToString(),
                Decision = "DENY",
                TraceId = traceId,
                DetailsJson = JsonSerializer.Serialize(new { is_allowed = false, reasons = decision.DeniedReasons, virtual_filters = decision.AppliedVirtualFilters })
            }, ct).ConfigureAwait(false);

            throw new GatewayForbiddenException($"Access to table '{table}' denied: {string.Join("; ", decision.DeniedReasons)}");
        }

        // Generate/Fetch query result via IDataSourceExecutor (SQL, Declarative HTTP, or Plugin)
        var maxRows = _options?.GraphQL?.MaxResponseRows > 0 ? _options.GraphQL.MaxResponseRows : 1000;
        var rowLimit = Math.Clamp(first ?? 50, 1, maxRows);

        var execArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["limit"] = rowLimit,
            ["offset"] = after ?? 0
        };
        if (queryArguments != null)
        {
            foreach (var (k, v) in queryArguments)
            {
                execArgs[k] = v;
            }
        }

        // Zero-Trust: Never request columns that are already denied by consent policy
        var authorizedColumns = metadata.Columns
            .Where(c => decision.GetColumnAccess(c.ColumnName) != ColumnAccessLevel.Deny)
            .Select(c => c.ColumnName)
            .ToList();

        // O1/O2/O13: requested columns are validated, never dropped silently (a dropped column used to widen the
        // projection to all columns). Unknown and denied columns are rejected with the same message.
        List<string> effectiveRequestedFields;
        var pageItems = new Dictionary<string, object?>(StringComparer.Ordinal);
        IDataSourceExecutor? executor;
        try
        {
            executor = _dataSourceExecutors?.FirstOrDefault(e => e.SupportedType == metadata.Table.DataSourceType);
            if (executor == null)
            {
                throw new GatewayNotImplementedException($"No executor is registered for DataSourceType '{metadata.Table.DataSourceType}'.");
            }

            effectiveRequestedFields = (requestedFields != null && requestedFields.Count > 0)
                ? await ResolveRequestedFieldsAsync(requestedFields, metadata, decision, authorizedColumns, tenantId, userSid, table, traceId, ct).ConfigureAwait(false)
                : authorizedColumns;

            // 4a.3 / 2.1: ordering, filtering and counting are pushed into the SQL statement; other data sources cannot do it (never ignored).
            if (orderBy is { Count: > 0 } || countTotal || filter != null)
            {
                if (metadata.Table.DataSourceType != DataSourceType.Sql)
                {
                    throw new GatewayNotImplementedException("Filtering, ordering and counting are only supported for SQL data sources.");
                }

                if (orderBy is { Count: > 0 })
                {
                    pageItems[TableQueryItems.OrderBy] = ValidateOrderBy(orderBy, metadata, decision);
                }

                if (filter != null)
                {
                    ValidateFilter(filter, metadata, decision);
                    pageItems[TableQueryItems.Filter] = filter;
                }

                if (countTotal)
                {
                    pageItems[TableQueryItems.CountTotal] = true;
                }
            }
        }
        catch (Exception ex)
        {
            // SR15-32: Validation/preparation failures are audited as DENY with predicate details
            await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = "TABLE_QUERY",
                ActorSid = userSid,
                TargetTable = table.ToString(),
                Decision = "DENY",
                TraceId = traceId,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    is_allowed = false,
                    reasons = new[] { ex.Message },
                    virtual_filters = decision.AppliedVirtualFilters,
                    filter = filter?.SqlPredicate,
                    order_by = orderBy?.Select(o => $"{o.Column} {(o.Descending ? "DESC" : "ASC")}").ToArray()
                })
            }, ct).ConfigureAwait(false);
            throw;
        }

        // SR15-32: ALLOW audit is recorded only after successful validation of all query parameters
        await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = "TABLE_QUERY",
            ActorSid = userSid,
            TargetTable = table.ToString(),
            Decision = "ALLOW",
            TraceId = traceId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                is_allowed = true,
                reasons = decision.DeniedReasons,
                virtual_filters = decision.AppliedVirtualFilters,
                filter = filter?.SqlPredicate,
                order_by = orderBy?.Select(o => $"{o.Column} {(o.Descending ? "DESC" : "ASC")}").ToArray()
            })
        }, ct).ConfigureAwait(false);

        // O10: bound concurrent reads of the same table by the same user (each slow read holds a database worker).
        var maxConcurrentReads = _options?.DataSources?.MaxConcurrentReadsPerUserAndTable ?? 0;
        using var readLease = AcquireReadLease(tenantId, userSid, table, maxConcurrentReads);

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rawRows;
        bool rlsPushdownAlreadyOccurred = false;
        bool inDbMaskingAlreadyOccurred = false;

        Autheris.Application.Connectors.IAutherisConnector? connector = null;
        bool canUseConnector = _connectorRegistry != null &&
            _connectorRegistry.TryGetConnectorForTable(table, out connector) &&
            connector != null &&
            (metadata.DataSourceType == DataSourceType.Sql ||
             (!string.Equals(connector.ConnectorId, "default-sql", StringComparison.OrdinalIgnoreCase) &&
              !string.Equals(connector.ConnectorId, "sql", StringComparison.OrdinalIgnoreCase)));

        if (canUseConnector && connector != null)
        {
            var session = new Autheris.Domain.Connectors.ConnectorSessionContext(
                Principal: principal,
                Tenant: tenantId,
                AccessDecision: decision,
                ProjectedColumns: effectiveRequestedFields,
                Arguments: execArgs,
                PushdownFilterSql: decision.CombinedRowFilterSql,
                Limit: rowLimit,
                Offset: after ?? 0,
                RequestHeaders: requestHeaders,
                Items: pageItems);

            rawRows = await Autheris.Application.Connectors.GovernedConnectorReader.ReadRawAsync(connector, session, metadata, maxRows: null, ct).ConfigureAwait(false);

            rlsPushdownAlreadyOccurred = session.Items.TryGetValue(Autheris.Application.Connectors.GovernedConnectorReader.RlsPushdownExecutedKey, out var p1) && p1 is true;
            inDbMaskingAlreadyOccurred = session.Items.TryGetValue(Autheris.Application.Connectors.GovernedConnectorReader.InDbColumnMaskingExecutedKey, out var m1) && m1 is true;
        }
        else
        {
            var execContext = new DataSourceExecutionContext(
                SourceName: metadata.Table.SourceName,
                Metadata: metadata,
                Principal: principal,
                AccessDecision: decision,
                Arguments: execArgs,
                RequestedFields: effectiveRequestedFields,
                RequestHeaders: requestHeaders,
                Limit: rowLimit,
                Offset: after ?? 0,
                Tenant: tenantId,
                Items: pageItems
            );

            rawRows = await executor.ExecuteAsync(execContext, ct);

            rlsPushdownAlreadyOccurred = execContext.Items.TryGetValue("RlsPushdownExecuted", out var p2) && p2 is true;
            inDbMaskingAlreadyOccurred = execContext.Items.TryGetValue("InDbColumnMaskingExecuted", out var m2) && m2 is true;
        }

        // Architecture 2: row filter (unless pushed down), masking exactly once and the response byte cap are the
        // same steps for connector and executor rows, and for every other connector caller (OLAP).
        var maxBytes = _options?.GraphQL?.MaxResponseBytes > 0 ? _options.GraphQL.MaxResponseBytes : 10 * 1024 * 1024;
        var processedRows = Autheris.Application.Connectors.GovernedConnectorReader.Apply(
            rawRows,
            metadata,
            decision,
            tenantId.Value,
            rlsPushdownAlreadyOccurred,
            inDbMaskingAlreadyOccurred,
            new Autheris.Application.Connectors.GovernedRowPolicy(
                _maskingProvider,
                _options?.DataMasking?.HmacKeyId,
                MaskingDisabled: _options?.IsColumnMaskingDisabled == true,
                MaxBytes: maxBytes));

        long? totalCount = null;
        if (countTotal)
        {
            // The count is only exact when the row filter ran in the same statement (not applied afterwards in memory).
            bool rowFilterInSql = rlsPushdownAlreadyOccurred || string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql);
            if (!rowFilterInSql || !pageItems.TryGetValue(TableQueryItems.TotalCount, out var countObj) || countObj is not long count)
            {
                throw new GatewayNotImplementedException("The total row count cannot be determined for this data source.");
            }

            totalCount = count;
        }

        return new TableQueryPage(processedRows, decision, totalCount);
    }

    /// <summary>
    /// 4a.3: ordering columns must exist and be readable in clear text. Ordering by a masked or denied column would
    /// reveal the order of the protected values; unknown and forbidden columns get the same message (no oracle).
    /// </summary>
    private static IReadOnlyList<TableOrderBy> ValidateOrderBy(IReadOnlyList<TableOrderBy> orderBy, TableMetadata metadata, TableAccessDecision decision)
    {
        var validated = new List<TableOrderBy>(orderBy.Count);
        foreach (var item in orderBy)
        {
            var column = metadata.GetColumn(item.Column);
            if (column == null || decision.GetEffectiveColumnAccess(column.ColumnName, metadata) != ColumnAccessLevel.Clear)
            {
                throw new GatewayInvalidQueryException($"The column '{item.Column}' in '$orderby' does not exist or cannot be used for ordering.");
            }

            validated.Add(item with { Column = column.ColumnName });
        }

        return validated;
    }

    /// <summary>
    /// Befund 2.1 & SR15-30: Zero-Trust filter column validation. Columns referenced in $filter must exist and be readable
    /// in clear text. Filtering on masked or denied columns would turn pushdown queries into an inference oracle.
    /// Unknown and forbidden/masked columns return the exact same GatewayInvalidQueryException to prevent column existence oracles.
    /// </summary>
    private static void ValidateFilter(TableFilterClause filter, TableMetadata metadata, TableAccessDecision decision)
    {
        foreach (var colName in filter.ReferencedColumns)
        {
            var column = metadata.GetColumn(colName);
            if (column == null || decision.GetEffectiveColumnAccess(column.ColumnName, metadata) != ColumnAccessLevel.Clear)
            {
                throw new GatewayInvalidQueryException($"The column '{colName}' in '$filter' does not exist or cannot be used for filtering.");
            }
        }
    }

    /// <summary>
    /// G5: Resolves catalog metadata and the effective access decision (consents, cache, Casbin ABAC and row filters)
    /// for one table, without auditing and without reading data. A denied decision is returned, not thrown.
    /// Used by ExecuteTableQueryAsync and by the GraphQL tree queries, so both apply the same decision.
    /// </summary>
    public async Task<ResolvedTableAccess> ResolveTableAccessAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        IReadOnlyList<string>? requestedFields,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        CancellationToken ct = default)
    {
        if (principal == null || principal.Identity?.IsAuthenticated != true)
        {
            if (_options?.IsAnonymousAccessAllowed == true)
            {
                var anonSid = new Sid("S-1-5-21-DEV-ANONYMOUS");
                principal = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.PrimarySid, anonSid.Value),
                    new Claim(ClaimTypes.Name, "DEV_ANONYMOUS"),
                    new Claim(ClaimTypes.Role, "AnonymousUser")
                ], "InsecureAnonymousAuth"));
            }
            else
            {
                throw new GatewayUnauthorizedException("Authentication is required to query tables.");
            }
        }

        var userSidNullable = principal.GetUserSid();
        if (userSidNullable == null)
        {
            throw new GatewayUnauthorizedException("No valid user SID in the authentication token.");
        }
        var userSid = userSidNullable.Value;

        // Resolve TenantId upfront for cache and consent isolation
        // RR-L4-02: identical semantics to SecurityContextFactory (single source of truth for HTTP ingress):
        // a tenant header may only select a tenant for canonical cluster admins whose identity was not asserted
        // via ForwardAuth headers, or for the development-only anonymous principal (danger_allow_anonymous_access).
        var tenantId = principal.GetTenantId();
        if (requestHeaders != null &&
            (requestHeaders.TryGetValue("X-Tenant-ID", out var tHeaders) || requestHeaders.TryGetValue("X-Tenant-Id", out tHeaders)) &&
            tHeaders.Length > 0 && TenantId.TryParse(tHeaders[0], out var parsedFromHeader) &&
            !string.Equals(parsedFromHeader.Value, tenantId.Value, StringComparison.OrdinalIgnoreCase))
        {
            var isAnonymousDev = principal.Identity?.IsAuthenticated != true;
            if (isAnonymousDev || Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal))
            {
                tenantId = parsedFromHeader;
            }
            // Non-admins keep the tenant from their token (header ignored, fail-safe). HTTP ingress already rejects
            // such mismatches with 403 in SecurityContextResolutionMiddleware.
        }

        // Verify table existence in metadata catalog (SG-12: inactive tables treated as not found)
        var metadata = await _metadataRepository.GetTableMetadataAsync(table, ct);
        if (metadata == null || !metadata.Table.IsActive)
        {
            throw new TableNotFoundException(table);
        }

        // Architecture 1: the shared access decision (ReBAC on query paths, consents with the decision cache, Casbin).
        var decision = await AccessPolicy().DecideAsync(
            TableAccessQuery.ForPrincipal(principal, userSid, tenantId, metadata, requestedFields), ct).ConfigureAwait(false);

        return new ResolvedTableAccess(metadata, decision, tenantId, userSid, principal);
    }

    /// <summary>
    /// O1/O2/O13: maps requested columns to their catalog spelling (deduplicated, request order). Throws for any column
    /// that is unknown or denied; the message is identical for both cases (no existence oracle). Denied columns are audited.
    /// </summary>
    private async Task<List<string>> ResolveRequestedFieldsAsync(
        IReadOnlyList<string> requestedFields,
        TableMetadata metadata,
        TableAccessDecision decision,
        IReadOnlyList<string> authorizedColumns,
        TenantId tenantId,
        Sid userSid,
        TableIdentifier table,
        string? traceId,
        CancellationToken ct)
    {
        var resolved = new List<string>(requestedFields.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requested in requestedFields)
        {
            var trimmed = requested?.Trim();
            var catalogCol = metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, trimmed, StringComparison.OrdinalIgnoreCase));
            if (catalogCol != null && decision.GetColumnAccess(catalogCol.ColumnName) == ColumnAccessLevel.Deny)
            {
                // O1: Gesperrte (Deny) Spalte: dieselbe Antwort wie unbekannt (kein Existenz-Orakel), Audit-Eintrag mit dem echten Grund.
                await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
                {
                    TenantId = tenantId,
                    EventType = "COLUMN_ACCESS_DENIED",
                    ActorSid = userSid,
                    TargetTable = table.ToString(),
                    Decision = "DENY",
                    TraceId = traceId ?? string.Empty,
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        column = catalogCol.ColumnName,
                        reason = $"Column '{catalogCol.ColumnName}' access is denied by governance policy."
                    })
                }, ct).ConfigureAwait(false);

                var echo = trimmed != null && EchoableColumnNameRegex().IsMatch(trimmed) ? trimmed : "(invalid name)";
                throw new GatewayInvalidQueryException($"The property '{echo}' does not exist or is not accessible.");
            }

            var catalogName = authorizedColumns.FirstOrDefault(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase));
            if (catalogName == null)
            {
                var echo = trimmed != null && EchoableColumnNameRegex().IsMatch(trimmed) ? trimmed : "(invalid name)";
                throw new GatewayInvalidQueryException($"The property '{echo}' does not exist or is not accessible.");
            }
            if (seen.Add(catalogName))
            {
                resolved.Add(catalogName);
            }
        }
        return resolved;
    }

    private IDisposable? AcquireReadLease(TenantId tenantId, Sid userSid, TableIdentifier table, int maxConcurrentReads)
    {
        if (_concurrencyGate == null || maxConcurrentReads <= 0)
        {
            return null;
        }

        var key = $"{tenantId.Value}|{userSid.Value}|{table.ToQualifiedName()}".ToLowerInvariant();
        return _concurrencyGate.TryEnter(key, maxConcurrentReads)
               ?? throw new GatewayThrottledException(ThrottledRetryAfterSeconds);
    }

    private readonly Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver? _mandatoryFilters;
    private readonly Autheris.Application.Governance.Contracts.ISchemaContractManager? _contractManager;
    private readonly IAccessProfileRepository? _accessProfileRepository;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache? _memoryCache;

    private TableAccessPolicy AccessPolicy() =>
        new(_consentRepository, _resolutionService,
            _cacheService,
            _policyEnforcementService ?? Autheris.Application.Policy.Services.NullPolicyEnforcementService.Instance,
            _rebacEvaluator ?? Autheris.Application.Security.Rebac.Services.NullRebacEvaluator.Instance,
            _clientIpResolver,
            _options ?? new GatewayOptions(),
            _mandatoryFilters ?? Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver.Instance,
            _contractManager,
            _accessProfileRepository,
            _memoryCache);

    /// <summary>
    /// Virtual filters: an in-memory row filter cannot evaluate their subqueries (it would return nothing and look like
    /// "no data"). Sources that filter in memory refuse the request instead (403 with reason).
    /// </summary>
    /// <summary>
    /// Virtual filters: an in-memory row filter cannot evaluate their subqueries (it would return nothing and look like
    /// "no data"). Sources that filter in memory refuse the request instead (403 with reason).
    /// </summary>
    public static void EnsureInMemoryFilterIsEnforceable(TableAccessDecision decision) =>
        InMemoryRowFilterEvaluator.EnsureInMemoryFilterIsEnforceable(decision);

    public static List<IReadOnlyDictionary<string, object?>> FilterRows(
        List<IReadOnlyDictionary<string, object?>> rows,
        string rowFilterSql,
        TableMetadata metadata) =>
        InMemoryRowFilterEvaluator.FilterRows(rows, rowFilterSql, metadata);

    internal static bool IsTypeStrictRowFilter(string normalizedSql, System.Data.DataTable table) =>
        InMemoryRowFilterEvaluator.IsTypeStrictRowFilter(normalizedSql, table);

    public async Task<TableAccessDecision> CheckTableAccessAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        CancellationToken ct = default)
    {
        using var _ = _drainController?.TrackQuery();
        if (principal == null || principal.Identity?.IsAuthenticated != true)
        {
            return TableAccessDecision.Denied(table, "Authentication required.");
        }

        var userSidNullable = principal.GetUserSid();
        if (userSidNullable == null)
        {
            return TableAccessDecision.Denied(table, "Valid user SID required.");
        }
        var userSid = userSidNullable.Value;

        var metadata = await _metadataRepository.GetTableMetadataAsync(table, ct);
        if (metadata == null || !metadata.Table.IsActive)
        {
            return TableAccessDecision.Denied(table, $"Table '{table}' not found in metadata catalog.");
        }

        var tenantId = principal.GetTenantId();

        // Architecture 1: same decision as ResolveTableAccessAsync (OData metadata must not show more than a query returns).
        var decision = await AccessPolicy().DecideAsync(TableAccessQuery.ForPrincipal(principal, userSid, tenantId, metadata), ct).ConfigureAwait(false);

        // F-OPS-02: W3C Trace Correlation
        var traceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId();
        await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = "CHILD_RELATION_CHECK",
            ActorSid = userSid,
            TargetTable = table.ToString(),
            Decision = decision.IsAllowed ? "ALLOW" : "DENY",
            TraceId = traceId,
            DetailsJson = JsonSerializer.Serialize(new { is_allowed = decision.IsAllowed, reasons = decision.DeniedReasons })
        }, ct);

        return decision;
    }

    public Task<IReadOnlyDictionary<string, List<InvoiceItemRecord>>> LoadInvoiceItemsBatchAsync(
        ClaimsPrincipal? principal,
        IReadOnlyList<string> invoiceIds,
        CancellationToken ct = default) =>
        _batchLoader.LoadInvoiceItemsBatchAsync(principal, invoiceIds, ct);

    internal static bool IsHmacRule(MaskingRule rule) => rule.IsHmac;

    /// <summary>
    /// SEC D-3: returns a tenant-scoped copy of HMAC rules and the rule itself for every other rule type.
    /// Idempotent: a rule that is already tenant-scoped is never scoped twice.
    /// </summary>
    internal static MaskingRule ScopeRuleForTenant(MaskingRule rule, string? tenant, string? defaultKeyId) =>
        !string.IsNullOrWhiteSpace(tenant) && IsHmacRule(rule) ? CreateTenantScopedHmacRule(rule, tenant, defaultKeyId) : rule;

    internal static MaskingRule CreateTenantScopedHmacRule(MaskingRule rule, string tenant, string? defaultKeyId) =>
        MaskingRule.CreateTenantScopedHmacRule(rule, tenant, defaultKeyId);
}
