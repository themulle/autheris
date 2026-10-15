namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Microsoft.Extensions.Options;
using Autheris.Domain.Options;
using static Autheris.Application.Mcp.Services.McpDatasetTools;

/// <summary>
/// MCP dataset tools (list_datasets, describe_dataset, sample_rows). Visibility follows the GraphQL catalog
/// (<see cref="McpCatalogVisibility"/>); rows are read only through <see cref="IGatewayExecutionService"/>, so consent,
/// ReBAC, Casbin, row filters and masking apply exactly as for REST and GraphQL.
/// </summary>
public sealed class McpDatasetCatalog : IMcpDatasetCatalog
{
    public const int MaxListedDatasets = 200;
    public const int DefaultSampleRows = 5;
    public const int MaxSampleRows = 20;
    private const int ExampleColumns = 10;

    /// <summary>Returned with every dataset list: GraphQL is the preferred query path.</summary>
    public const string Guidance =
        "Query data with GraphQL: call the query_graphql tool (or POST the query to the GraphQL endpoint). " +
        "describe_dataset returns the GraphQL field, the filter and sort types and an example query for each dataset. " +
        "The other endpoints are listed for clients that need a specific protocol.";

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IConsentRepository _consentRepository;
    private readonly IGatewayExecutionService _gatewayExecutionService;
    private readonly IGoldenQueryService? goldenQueryService;
    private readonly IOptions<GatewayOptions>? options;
    private readonly IGraphQlCatalogMap? graphQlMap;
    private readonly Autheris.Application.Governance.Interfaces.IEffectiveAccessExplanationService? explanationService;
    private readonly IDataOwnershipRepository? dataOwnershipRepository;
    private readonly IConsentApprovalRepository? approvalRepository;
    private readonly Autheris.Application.Catalog.Profiling.IColumnValueProfileService? profileService;
    private readonly Autheris.Application.Catalog.Profiling.ISyntheticSampleRowGenerator? syntheticRowGenerator;
    private readonly IAuditLogRepository? auditLogRepository;
    private readonly Autheris.Application.Governance.Interfaces.ISchemaSunsettingService? sunsettingService;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public McpDatasetCatalog(
        ITableMetadataRepository metadataRepository,
        IConsentRepository consentRepository,
        IGatewayExecutionService gatewayExecutionService,
        IAuditLogRepository auditLogRepository,
        IGoldenQueryService? goldenQueryService = null,
        IOptions<GatewayOptions>? options = null,
        IGraphQlCatalogMap? graphQlMap = null,
        Autheris.Application.Governance.Interfaces.IEffectiveAccessExplanationService? explanationService = null,
        IDataOwnershipRepository? dataOwnershipRepository = null,
        IConsentApprovalRepository? approvalRepository = null,
        Autheris.Application.Catalog.Profiling.IColumnValueProfileService? profileService = null,
        Autheris.Application.Catalog.Profiling.ISyntheticSampleRowGenerator? syntheticRowGenerator = null,
        Autheris.Application.Governance.Interfaces.ISchemaSunsettingService? sunsettingService = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _gatewayExecutionService = gatewayExecutionService ?? throw new ArgumentNullException(nameof(gatewayExecutionService));
        this.auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        this.goldenQueryService = goldenQueryService;
        this.options = options;
        this.graphQlMap = graphQlMap;
        this.explanationService = explanationService;
        this.dataOwnershipRepository = dataOwnershipRepository;
        this.approvalRepository = approvalRepository;
        this.profileService = profileService;
        this.syntheticRowGenerator = syntheticRowGenerator;
        this.sunsettingService = sunsettingService;
    }

    public McpDatasetCatalog(
        ITableMetadataRepository metadataRepository,
        IConsentRepository consentRepository,
        IGatewayExecutionService gatewayExecutionService,
        IGoldenQueryService? goldenQueryService = null,
        IOptions<GatewayOptions>? options = null,
        IGraphQlCatalogMap? graphQlMap = null,
        Autheris.Application.Governance.Interfaces.IEffectiveAccessExplanationService? explanationService = null,
        IDataOwnershipRepository? dataOwnershipRepository = null,
        IConsentApprovalRepository? approvalRepository = null,
        Autheris.Application.Catalog.Profiling.IColumnValueProfileService? profileService = null,
        Autheris.Application.Catalog.Profiling.ISyntheticSampleRowGenerator? syntheticRowGenerator = null,
        Autheris.Application.Governance.Interfaces.ISchemaSunsettingService? sunsettingService = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _gatewayExecutionService = gatewayExecutionService ?? throw new ArgumentNullException(nameof(gatewayExecutionService));
        this.goldenQueryService = goldenQueryService;
        this.options = options;
        this.graphQlMap = graphQlMap;
        this.explanationService = explanationService;
        this.dataOwnershipRepository = dataOwnershipRepository;
        this.approvalRepository = approvalRepository;
        this.profileService = profileService;
        this.syntheticRowGenerator = syntheticRowGenerator;
        this.sunsettingService = sunsettingService;
    }

    public Task<McpDatasetList> ListDatasetsAsync(
        ClaimsPrincipal principal,
        string? search,
        string? domain,
        CancellationToken ct) =>
        ListDatasetsAsync(principal, search, domain, offset: null, limit: null, tag: null, ct);

    public Task<McpDatasetList> ListDatasetsAsync(
        ClaimsPrincipal principal,
        string? search,
        string? domain,
        int? offset,
        int? limit,
        CancellationToken ct) =>
        ListDatasetsAsync(principal, search, domain, offset, limit, tag: null, ct);

    public async Task<McpDatasetList> ListDatasetsAsync(
        ClaimsPrincipal principal,
        string? search = null,
        string? domain = null,
        int? offset = null,
        int? limit = null,
        string? tag = null,
        CancellationToken ct = default)
    {
        var visible = await VisibleTablesAsync(principal, ct).ConfigureAwait(false);

        var matches = visible
            .Where(t => string.IsNullOrWhiteSpace(domain) || string.Equals(t.Identifier.Domain, domain.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(t => string.IsNullOrWhiteSpace(search) || Matches(t, search.Trim()))
            .Where(t => string.IsNullOrWhiteSpace(tag) || (t.Table.Tags != null && t.Table.Tags.Any(tg => string.Equals(tg, tag.Trim(), StringComparison.OrdinalIgnoreCase))))
            .OrderBy(t => DatasetId(t.Identifier), StringComparer.Ordinal)
            .ToList();

        var effectiveOffset = Math.Max(0, offset ?? 0);
        var effectiveLimit = Math.Clamp(limit ?? 50, 1, MaxListedDatasets);

        var pagedMatches = matches.Skip(effectiveOffset).Take(effectiveLimit).ToList();
        var datasets = new List<McpDatasetSummary>(pagedMatches.Count);

        var (activeConsents, pendingRequests, isAdmin) = await GetCallerConsentsAndRequestsAsync(principal, ct).ConfigureAwait(false);

        foreach (var t in pagedMatches)
        {
            var graphQl = graphQlMap == null ? null : await graphQlMap.GetTableAsync(t.Identifier, ct).ConfigureAwait(false);
            var status = options?.Value.IsMcpAuthBypassed == true
                ? "GRANTED"
                : CatalogVisibility.ComputeAccessStatus(t.Identifier, activeConsents, pendingRequests, isAdmin);

            // 4b.1: Only short description in summary; long description only in describe_dataset
            datasets.Add(new McpDatasetSummary(
                DatasetId(t.Identifier),
                t.Table.Description,
                t.Table.Sensitivity,
                t.Columns.Count,
                graphQl?.QueryField,
                Tags: t.Table.Tags ?? Array.Empty<string>(),
                AccessStatus: status));
        }

        var total = matches.Count;
        var hasMore = effectiveOffset + datasets.Count < total;
        int? nextOffset = hasMore ? effectiveOffset + datasets.Count : null;

        return new McpDatasetList(
            datasets,
            total,
            hasMore,
            Guidance,
            Endpoints(),
            effectiveOffset,
            effectiveLimit,
            nextOffset);
    }

    public async Task<McpDatasetDescription> DescribeDatasetAsync(ClaimsPrincipal principal, string dataset, CancellationToken ct = default)
    {
        var table = await FindVisibleAsync(principal, dataset, ct).ConfigureAwait(false);
        var id = table.Identifier;

        var (activeConsents, pendingRequests, isAdmin) = await GetCallerConsentsAndRequestsAsync(principal, ct).ConfigureAwait(false);
        var accessStatus = options?.Value.IsMcpAuthBypassed == true
            ? "GRANTED"
            : CatalogVisibility.ComputeAccessStatus(id, activeConsents, pendingRequests, isAdmin);

        bool canViewProfile = string.Equals(accessStatus, "GRANTED", StringComparison.OrdinalIgnoreCase) ||
                              table.ReleaseProfileToCatalogViewers;

        var (columnAccessLevels, hasActiveRowFilters) = ComputeTableColumnAccessAndFilters(table, activeConsents, isAdmin);
        var effectiveProfileService = profileService ?? new Autheris.Application.Catalog.Profiling.ColumnValueProfileService();
        var sunsetRules = await LoadSunsettingRulesAsync(ct).ConfigureAwait(false);

        var graphQl = graphQlMap == null ? null : await graphQlMap.GetTableAsync(id, ct).ConfigureAwait(false);
        var graphQlColumns = (graphQl?.Columns ?? []).ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);

        // SD-4: only keys and indexes that lie wholly within the columns listed for this caller.
        var keyView = Autheris.Application.Data.Services.TableKeyVisibility.Restrict(table, table.Columns);
        var primaryKeys = new HashSet<string>(keyView.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);
        var columns = table.Columns
            .Select(c =>
            {
                graphQlColumns.TryGetValue(c.ColumnName, out var field);
                var deprecation = ResolveFieldDeprecation(id, c, sunsetRules);
                ColumnValueProfile? valueProfile = null;
                if (canViewProfile)
                {
                    var effectiveLevel = columnAccessLevels.TryGetValue(c.ColumnName, out var lvl)
                        ? lvl
                        : (c.IsSensitive || (table.ColumnMaskingRules != null && table.ColumnMaskingRules.ContainsKey(c.ColumnName))
                            ? Autheris.Domain.Interfaces.ColumnAccessLevel.Mask
                            : Autheris.Domain.Interfaces.ColumnAccessLevel.Clear);
                    valueProfile = effectiveProfileService.GetColumnProfile(table, c, effectiveLevel, hasActiveRowFilters);
                }

                return new McpDatasetColumn(
                    c.ColumnName,
                    c.DataType,
                    c.Description,
                    c.IsSensitive,
                    primaryKeys.Contains(c.ColumnName),
                    field?.Field,
                    field?.GraphQlType,
                    c.Semantics,
                    Deprecation: deprecation,
                    ValueProfile: valueProfile);
            })
            .ToList();

        IReadOnlyList<McpDatasetExample> examples = [];
        if (goldenQueryService != null)
        {
            var goldens = await goldenQueryService.GetGoldenQueriesAsync(id.Domain, id.TableName, ct).ConfigureAwait(false);
            examples = goldens
                .Select(g => new McpDatasetExample(g.Title, g.Description, g.QueryText, g.VariablesJson))
                .ToList();
        }

        string? dataOwner = null;
        List<string>? dataOwnerDelegates = null;
        if (dataOwnershipRepository != null)
        {
            try
            {
                var owners = await dataOwnershipRepository.GetDataOwnersForTableAsync(id, ct).ConfigureAwait(false);
                var activeOwners = owners.Where(o => o.IsActive).ToList();
                if (activeOwners.Count > 0)
                {
                    var primary = activeOwners[0];
                    dataOwner = !string.IsNullOrWhiteSpace(primary.DisplayName) && !string.IsNullOrWhiteSpace(primary.Email)
                        ? $"{primary.DisplayName} <{primary.Email}>"
                        : (!string.IsNullOrWhiteSpace(primary.DisplayName)
                            ? primary.DisplayName
                            : (!string.IsNullOrWhiteSpace(primary.Email) ? primary.Email : primary.AdAccount));

                    if (activeOwners.Count > 1)
                    {
                        dataOwnerDelegates = activeOwners.Skip(1).Select(o =>
                            !string.IsNullOrWhiteSpace(o.DisplayName) && !string.IsNullOrWhiteSpace(o.Email)
                                ? $"{o.DisplayName} <{o.Email}>"
                                : (!string.IsNullOrWhiteSpace(o.DisplayName) ? o.DisplayName : (o.Email ?? o.AdAccount))
                        ).ToList();
                    }
                }
            }
            catch
            {
                // Fail-safe
            }
        }

        return new McpDatasetDescription(
            DatasetId(id),
            id.Domain,
            id.Schema,
            id.TableName,
            table.Table.Description,
            table.Table.LongDescription,
            table.Table.Sensitivity,
            table.Table.RequiresFourEyes,
            columns,
            examples,
            graphQl == null ? null : await GraphQlAccessAsync(principal, graphQl, columns, ct).ConfigureAwait(false),
            DatasetAccess(id),
            Tags: table.Table.Tags ?? Array.Empty<string>(),
            DataOwner: dataOwner,
            DataOwnerDelegates: dataOwnerDelegates,
            AccessStatus: accessStatus,
            Indexes: keyView.Indexes);
    }

    private async Task<IReadOnlyList<FieldSunsettingRule>> LoadSunsettingRulesAsync(CancellationToken ct)
    {
        var list = new List<FieldSunsettingRule>();
        if (options?.Value.FieldSunsetting?.Rules is { Count: > 0 } configured)
        {
            list.AddRange(configured);
        }

        if (sunsettingService != null)
        {
            try
            {
                var serviceRules = await sunsettingService.GetRulesAsync(ct).ConfigureAwait(false);
                if (serviceRules is { Count: > 0 })
                {
                    list.AddRange(serviceRules);
                }
            }
            catch
            {
                // Fail-safe
            }
        }

        return list;
    }

    internal static FieldDeprecationInfo? ResolveFieldDeprecation(
        TableIdentifier tableId,
        TableColumn column,
        IReadOnlyList<FieldSunsettingRule>? rules)
    {
        if (rules is { Count: > 0 })
        {
            var fullId = DatasetId(tableId);
            var schemaTable = $"{tableId.Schema}.{tableId.TableName}";
            var match = rules.FirstOrDefault(r =>
                r.IsActive &&
                string.Equals(r.FieldName, column.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(r.TargetTable, fullId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(r.TargetTable, schemaTable, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(r.TargetTable, tableId.TableName, StringComparison.OrdinalIgnoreCase)));

            if (match != null)
            {
                return new FieldDeprecationInfo(
                    match.DeprecatedAt,
                    match.SunsetAt,
                    match.ReplacementField,
                    match.DeprecationReason);
            }
        }

        if (column.Meta is { Count: > 0 } meta)
        {
            meta.TryGetValue("deprecated_since", out var depStr);
            if (string.IsNullOrWhiteSpace(depStr))
            {
                meta.TryGetValue("deprecated_at", out depStr);
            }

            meta.TryGetValue("sunset_date", out var sunStr);
            if (string.IsNullOrWhiteSpace(sunStr))
            {
                meta.TryGetValue("sunset_at", out sunStr);
            }

            bool hasDep = !string.IsNullOrWhiteSpace(depStr) &&
                          DateTimeOffset.TryParse(depStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out _);
            bool hasSun = !string.IsNullOrWhiteSpace(sunStr) &&
                          DateTimeOffset.TryParse(sunStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out _);

            if (hasDep || hasSun)
            {
                var parsedDep = hasDep
                    ? DateTimeOffset.Parse(depStr!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal)
                    : DateTimeOffset.Parse(sunStr!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal).AddDays(-30);

                var parsedSun = hasSun
                    ? DateTimeOffset.Parse(sunStr!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal)
                    : parsedDep.AddDays(90);

                meta.TryGetValue("replacement_field", out var replacement);
                if (!meta.TryGetValue("deprecation_reason", out var reason))
                {
                    meta.TryGetValue("reason", out reason);
                }

                return new FieldDeprecationInfo(
                    parsedDep,
                    parsedSun,
                    string.IsNullOrWhiteSpace(replacement) ? null : replacement.Trim(),
                    string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());
            }
        }

        return null;
    }

    private static (IReadOnlyDictionary<string, Autheris.Domain.Interfaces.ColumnAccessLevel> AccessLevels, bool HasActiveRowFilters) ComputeTableColumnAccessAndFilters(
        TableMetadata table,
        IReadOnlyList<Consent> activeConsents,
        bool isAdmin)
    {
        var tableConsents = activeConsents
            .Where(c => c.TableIdentifier == table.Identifier)
            .ToList();

        var allowConsents = tableConsents
            .Where(c => c.Effect == ConsentEffect.Allow)
            .ToList();

        bool hasActiveRowFilters = !string.IsNullOrWhiteSpace(table.Table.RlsExpression) ||
                                   allowConsents.Any(c => c.RowFilters is { Count: > 0 } || !string.IsNullOrWhiteSpace(c.RowFilterCondition));

        var levels = new Dictionary<string, Autheris.Domain.Interfaces.ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in table.Columns)
        {
            if (isAdmin)
            {
                levels[col.ColumnName] = Autheris.Domain.Interfaces.ColumnAccessLevel.Clear;
                continue;
            }

            bool isExplicitlyDenied = tableConsents
                .SelectMany(c => c.ColumnRules)
                .Any(cr => string.Equals(cr.ColumnName, col.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                           cr.AccessLevel == Autheris.Domain.Interfaces.ColumnAccessLevel.Deny) ||
                (table.ColumnMaskingRules != null &&
                 table.ColumnMaskingRules.TryGetValue(col.ColumnName, out var mr) &&
                 string.Equals(mr.RuleType, "DENY", StringComparison.OrdinalIgnoreCase));

            if (isExplicitlyDenied)
            {
                levels[col.ColumnName] = Autheris.Domain.Interfaces.ColumnAccessLevel.Deny;
                continue;
            }

            bool hasExplicitClear = allowConsents
                .SelectMany(c => c.ColumnRules)
                .Any(cr => string.Equals(cr.ColumnName, col.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                           cr.AccessLevel == Autheris.Domain.Interfaces.ColumnAccessLevel.Clear);

            bool hasExplicitMask = allowConsents
                .SelectMany(c => c.ColumnRules)
                .Any(cr => string.Equals(cr.ColumnName, col.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                           cr.AccessLevel == Autheris.Domain.Interfaces.ColumnAccessLevel.Mask);

            bool isSensitiveInCatalog = col.IsSensitive ||
                                        (table.ColumnMaskingRules != null && table.ColumnMaskingRules.ContainsKey(col.ColumnName));

            if ((isSensitiveInCatalog && !hasExplicitClear) || (hasExplicitMask && !hasExplicitClear))
            {
                levels[col.ColumnName] = Autheris.Domain.Interfaces.ColumnAccessLevel.Mask;
            }
            else
            {
                levels[col.ColumnName] = Autheris.Domain.Interfaces.ColumnAccessLevel.Clear;
            }
        }

        return (levels, hasActiveRowFilters);
    }

    private async Task<(IReadOnlyList<Consent> ActiveConsents, IReadOnlyList<ConsentRequest> PendingRequests, bool IsAdmin)> GetCallerConsentsAndRequestsAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var roles = principal.GetUserRoles();
        var isAdmin = roles.Contains("GovernanceAdmin") || roles.Contains("ClusterAdmin");
        if (isAdmin)
        {
            return (Array.Empty<Consent>(), Array.Empty<ConsentRequest>(), true);
        }

        var userSid = principal.GetUserSid();
        if (userSid == null)
        {
            return (Array.Empty<Consent>(), Array.Empty<ConsentRequest>(), false);
        }

        var tenantId = principal.GetTenantId();
        var subjects = principal.GetGroupSids().Append(userSid.Value).ToList();
        var activeConsents = await _consentRepository.GetAllActiveConsentsForSubjectsAsync(
            subjects, roles, DateTimeOffset.UtcNow, tenantId, ct).ConfigureAwait(false);

        IReadOnlyList<ConsentRequest> pendingRequests = Array.Empty<ConsentRequest>();
        if (approvalRepository != null)
        {
            try
            {
                pendingRequests = await approvalRepository.GetRequestsByRequesterSidAsync(userSid.Value, "PENDING", ct).ConfigureAwait(false);
            }
            catch
            {
                // Fail-safe
            }
        }

        return (activeConsents, pendingRequests, false);
    }

    public async Task<EffectiveTableAccessDto> DescribeMyAccessAsync(ClaimsPrincipal principal, string dataset, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!TryParseDatasetId(dataset, out var tableId))
        {
            throw new ArgumentException($"Invalid dataset format: '{dataset}'. Expected 'domain.schema.table' or 'domain.table'.", nameof(dataset));
        }

        if (explanationService != null)
        {
            return await explanationService.ExplainMyAccessAsync(principal, tableId, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("EffectiveAccessExplanationService is not configured.");
    }

    public Task<McpDatasetSample> SampleRowsAsync(ClaimsPrincipal principal, string dataset, int? count, CancellationToken ct = default) =>
        SampleRowsAsync(principal, dataset, count, synthetic: false, ct);

    public async Task<McpDatasetSample> SampleRowsAsync(ClaimsPrincipal principal, string dataset, int? count, bool synthetic, CancellationToken ct = default)
    {
        var table = await FindVisibleAsync(principal, dataset, ct).ConfigureAwait(false);
        var rowCount = Math.Clamp(count ?? DefaultSampleRows, 1, MaxSampleRows);

        if (synthetic)
        {
            var (activeConsents, pendingRequests, isAdmin) = await GetCallerConsentsAndRequestsAsync(principal, ct).ConfigureAwait(false);
            var accessStatus = options?.Value.IsMcpAuthBypassed == true
                ? "GRANTED"
                : CatalogVisibility.ComputeAccessStatus(table.Identifier, activeConsents, pendingRequests, isAdmin);

            bool isGranted = string.Equals(accessStatus, "GRANTED", StringComparison.OrdinalIgnoreCase);
            if (!isGranted && !table.ReleaseProfileToCatalogViewers)
            {
                throw new GatewayForbiddenException("Synthetic sample rows require either an active table consent or ReleaseProfileToCatalogViewers enabled by the data owner.");
            }

            var (columnAccessLevels, hasActiveRowFilters) = ComputeTableColumnAccessAndFilters(table, activeConsents, isAdmin);
            var generator = syntheticRowGenerator ?? new Autheris.Application.Catalog.Profiling.SyntheticSampleRowGenerator(profileService);
            var syntheticRows = generator.GenerateRows(table, rowCount, columnAccessLevels, hasActiveRowFilters);
            var datasetId = DatasetId(table.Identifier);
            var queryText = $"sample_rows(dataset={datasetId}, count={rowCount}, synthetic=true)";

            if (auditLogRepository != null)
            {
                var actorSid = principal.GetUserSid() ?? new Sid(principal.Identity?.Name ?? "anonymous");
                await auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
                {
                    TenantId = principal.GetTenantId(),
                    OccurredAt = DateTimeOffset.UtcNow,
                    EventType = Autheris.Domain.Audit.AuditEventTypes.MetadataSyntheticSample,
                    ActorSid = actorSid,
                    TargetTable = datasetId,
                    Query = queryText,
                    Decision = "ALLOW",
                    DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        query = queryText,
                        dataset = datasetId,
                        count = rowCount,
                        synthetic = true
                    })
                }, ct).ConfigureAwait(false);
            }

            return new McpDatasetSample(datasetId, syntheticRows.Count, syntheticRows, IsSynthetic: true);
        }

        var (rows, decision) = await _gatewayExecutionService.ExecuteTableQueryAsync(
            principal,
            table.Identifier,
            first: rowCount,
            after: 0,
            queryArguments: null,
            requestedFields: null,
            requestHeaders: null,
            ct: ct).ConfigureAwait(false);

        if (!decision.IsAllowed)
        {
            throw new GatewayForbiddenException("Access denied by data governance policy.");
        }

        return new McpDatasetSample(DatasetId(table.Identifier), rows.Count, rows, IsSynthetic: false);
    }

    private string GraphQlEndpoint => options?.Value.GraphQL.EndpointPath is { Length: > 0 } path ? path : "/graphql";

    private async Task<McpGraphQlAccess> GraphQlAccessAsync(
        ClaimsPrincipal principal,
        GraphQlTableMapping graphQl,
        IReadOnlyList<McpDatasetColumn> columns,
        CancellationToken ct)
    {
        // Relations are offered only to datasets the caller can see, so hidden table names do not leak.
        var visibleIds = (await VisibleTablesAsync(principal, ct).ConfigureAwait(false))
            .Select(t => DatasetId(t.Identifier))
            .ToHashSet(StringComparer.Ordinal);
        var relations = graphQl.Relations
            .Select(r => new McpGraphQlRelation(r.Field, DatasetId(r.Target), r.IsList))
            .Where(r => visibleIds.Contains(r.Dataset))
            .ToList();

        var exampleFields = columns.Where(c => c.GraphQlField != null).Take(ExampleColumns).Select(c => c.GraphQlField);
        var example = $"query {{ {graphQl.QueryField}(first: 10) {{ {string.Join(' ', exampleFields)} }} }}";
        var arguments =
            $"where: {graphQl.FilterType} – per field eq, neq, gt, gte, lt, lte, in, nin, contains, startsWith, endsWith, isNull; " +
            $"combine with and/or (lists) and not. orderBy: [{graphQl.OrderByType}] – {{ field: ASC | DESC }}. " +
            "first: Int (default 100), offset: Int. Always pass a small first: queries above the complexity budget are rejected. " +
            "Filter and sort only on columns you can read in clear text.";

        return new McpGraphQlAccess(GraphQlEndpoint, graphQl.QueryField, graphQl.TypeName, graphQl.FilterType, graphQl.OrderByType, arguments, example, relations);
    }

    /// <summary>The non-GraphQL ways to read one dataset.</summary>
    private IReadOnlyList<McpAccessPath> DatasetAccess(TableIdentifier id)
    {
        var o = options?.Value ?? new GatewayOptions();
        var paths = new List<McpAccessPath>();

        if (o.OData.Enabled)
        {
            paths.Add(new("OData v4", "GET", $"/odata/v4/{id.Domain}/{id.Schema}/{id.TableName}",
                "OData query options $select, $filter, $orderby, $top, $skip. JSON with OData annotations."));
            paths.Add(new("OpenAPI (REST)", "GET", $"/odata/v4/{id.Domain}/openapi.json",
                "OpenAPI 3.1 description of the REST routes of this domain."));
        }

        paths.Add(new("MCP sample_rows", "MCP", SampleRows, "A few rows of this dataset, for a first look."));

        if (o.Arrow.Enabled)
        {
            paths.Add(new("Arrow IPC export", "POST", "/api/v1/export/arrow",
                $"Body {{\"table\": \"{id.Domain}.{id.Schema}.{id.TableName}\"}}: columnar bulk export (application/vnd.apache.arrow.stream)."));
        }

        return paths;
    }

    /// <summary>All enabled protocols of the gateway, GraphQL first.</summary>
    private IReadOnlyList<McpAccessPath> Endpoints()
    {
        var o = options?.Value ?? new GatewayOptions();
        var endpoints = new List<McpAccessPath>
        {
            new("GraphQL", "POST", GraphQlEndpoint,
                "Preferred query path: query, filter, sort, page and follow relations between datasets in one request. Use the query_graphql tool.",
                Preferred: true)
        };

        if (o.OData.Enabled)
        {
            endpoints.Add(new("OData v4", "GET", "/odata/v4",
                "Service document; /odata/v4/$metadata is the CSDL schema, /odata/v4/{domain}/{schema}/{table} the entity sets."));
            endpoints.Add(new("OpenAPI (REST)", "GET", "/api/v1/openapi/index", "Index of the OpenAPI 3.1 documents per domain."));
        }

        if (o.WebSql.Enabled)
        {
            endpoints.Add(new("WebSQL", "POST", "/api/v1/sql",
                "Body {\"sql\", \"parameters\", \"dataSource\"}: governed SQL in Trino syntax with 3-part names (<catalog>.<schema>.<table>) or 2-part names (<schema>.<table>)."));
            endpoints.Add(new("Trino Protocol", "POST", "/v1/statement",
                "Standard Trino REST Client protocol: query in body or JSON, X-Trino-Wait-Timeout for sync/async execution, continuation via /v1/statement/queued/{id}."));
        }

        if (o.SqlEndpoints.Enabled)
        {
            endpoints.Add(new("Saved SQL queries", "GET", "/api/v1/queries", "Lists the curated SQL endpoints; call one with GET or POST /api/v1/queries/{name}."));
        }

        if (o.SqlEndpoints.Procedures.Enabled)
        {
            endpoints.Add(new("Stored procedures", "GET", "/api/v1/procedures", "Lists the published procedures; call one with GET or POST /api/v1/procedures/{name}."));
        }

        if (o.DuckDbOlap.Enabled)
        {
            endpoints.Add(new("DuckDB OLAP", "POST", "/api/v1/olap/query",
                "Body {\"sql\", \"tableNames\", \"limit\"}: analytical SQL across several datasets."));
        }

        if (o.Arrow.Enabled)
        {
            endpoints.Add(new("Arrow IPC export", "POST", "/api/v1/export/arrow", "Columnar bulk export of one dataset."));
            if (o.Arrow.EnableFlightSql)
            {
                endpoints.Add(new("Arrow Flight SQL", "POST", "/api/v1/flight/sql", "Arrow Flight SQL protocol for analytical clients."));
            }
        }

        if (o.Lakehouse.Enabled)
        {
            endpoints.Add(new("Iceberg REST Catalog", "GET", "/v1/namespaces", "Apache Iceberg REST Catalog protocol endpoint."));
        }

        return endpoints;
    }

    /// <summary>Dataset ids use the same <c>domain.schema.table</c> form as ReBAC objects.</summary>
    public static string DatasetId(TableIdentifier id) => $"{id.Domain}.{id.Schema}.{id.TableName}".ToLowerInvariant();

    /// <summary>Parses a <c>domain.schema.table</c> dataset id.</summary>
    public static bool TryParseDatasetId(string? dataset, out TableIdentifier id)
    {
        id = default;
        var parts = (dataset ?? string.Empty).Trim().Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        id = new TableIdentifier(parts[0], parts[1], parts[2]);
        return true;
    }

    private async Task<TableMetadata> FindVisibleAsync(ClaimsPrincipal principal, string dataset, CancellationToken ct)
    {
        if (!TryParseDatasetId(dataset, out var requested))
        {
            throw new ArgumentException("The dataset id must have the form 'domain.schema.table'.", nameof(dataset));
        }

        var visible = await VisibleTablesAsync(principal, ct).ConfigureAwait(false);
        var requestedId = DatasetId(requested);

        // A table the caller may not see is reported exactly like one that does not exist (no enumeration oracle).
        return visible.FirstOrDefault(t => string.Equals(DatasetId(t.Identifier), requestedId, StringComparison.Ordinal))
            ?? throw new TableNotFoundException(requested);
    }

    private async Task<IReadOnlyList<TableMetadata>> VisibleTablesAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var allTables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
        // SEC H-02: only the explicit (production-blocked) MCP auth bypass opens the whole catalog.
        return options?.Value.IsMcpAuthBypassed == true
            ? allTables
            : await McpCatalogVisibility.VisibleTablesAsync(allTables, principal, _consentRepository, ct).ConfigureAwait(false);
    }

    private static bool Matches(TableMetadata table, string search) =>
        DatasetId(table.Identifier).Contains(search, StringComparison.OrdinalIgnoreCase) ||
        (table.Table.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
        table.Columns.Any(c =>
            c.ColumnName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            (c.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
}
