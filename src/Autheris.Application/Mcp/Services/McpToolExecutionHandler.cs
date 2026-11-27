namespace Autheris.Application.Mcp.Services;

using Autheris.Application.Tenancy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TrinoSqlEngine.Ast.Emit;

/// <summary>
/// Pipeline handler for executing MCP tools without bloating AiDataGuardrailService or GatewayMcpQueryExecutor.
/// </summary>
public sealed class McpToolExecutionHandler : IMcpToolExecutionHandler
{
    // Issue #702: no null members and no empty objects in tool results.
    private static readonly JsonSerializerOptions JsonOpts = McpResultJson.Options;

    // Structured tool errors (isError:true) use the stable codes of the MCP query executor; the messages are fixed texts.
    private const string InvalidParamsCode = "INVALID_PARAMS";
    private const string ForbiddenCode = "FORBIDDEN";
    private const string AccessDeniedMessage = "Access denied by data governance policy.";
    private const string InvalidSearchQueryMessage = "The search query is invalid or too long.";

    /// <summary>Documented maximum and default of the candidate tables search_catalog / find_relevant_tables return.</summary>
    internal const int MaxCatalogSearchResults = 20;
    internal const int DefaultCatalogSearchResults = 5;

    private readonly IGovernedSqlExecutionService _sqlExecutionService;
    private readonly IGovernedDataQueryService _dataQueryService;
    private readonly ICatalogDiscoveryService _catalogDiscoveryService;
    private readonly IApiDispatcherService _apiDispatcherService;
    private readonly ILineageGraphStore _lineageGraphStore;
    private readonly ITableMetadataRepository _metadataRepo;
    private readonly ISemanticCatalogService? _semanticCatalog;
    private readonly Autheris.Application.Analytics.ISemanticMetricQueryService? _metricQueryService;
    private readonly TenantId? _catalogOwner;
    private readonly GatewayOptions? _gatewayOptions;
    private readonly Autheris.Application.Procedures.Interfaces.IProcedureCatalog? _procedureCatalog;
    private readonly Autheris.Application.Procedures.Interfaces.IProcedureExecutionService? _procedureExecutionService;
    private readonly IGraphQlCatalogMap? _graphQlMap;
    private readonly ICatalogSearchEngine? _searchEngine;
    private readonly Autheris.Application.Catalog.Visibility.IContainerCatalogService? _containerCatalog;
    private readonly ILogger<McpToolExecutionHandler> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Autheris.Application.Common.Quota.IScopeQueryExecutionGuard _scopeGuard;

    public McpToolExecutionHandler(
        IGovernedSqlExecutionService sqlExecutionService,
        IGovernedDataQueryService dataQueryService,
        ICatalogDiscoveryService catalogDiscoveryService,
        IApiDispatcherService apiDispatcherService,
        ILineageGraphStore lineageGraphStore,
        ITableMetadataRepository metadataRepo,
        ISemanticCatalogService? semanticCatalog = null,
        Autheris.Application.Analytics.ISemanticMetricQueryService? metricQueryService = null,
        IOptions<GatewayOptions>? gatewayOptions = null,
        Autheris.Application.Procedures.Interfaces.IProcedureCatalog? procedureCatalog = null,
        Autheris.Application.Procedures.Interfaces.IProcedureExecutionService? procedureExecutionService = null,
        IGraphQlCatalogMap? graphQlMap = null,
        ICatalogSearchEngine? searchEngine = null,
        ILogger<McpToolExecutionHandler>? logger = null,
        TimeProvider? timeProvider = null,
        Autheris.Application.Catalog.Visibility.IContainerCatalogService? containerCatalog = null,
        Autheris.Application.Common.Quota.IScopeQueryExecutionGuard? scopeGuard = null)
    {
        _containerCatalog = containerCatalog;
        _scopeGuard = scopeGuard ?? Autheris.Application.Common.Quota.NullScopeQueryExecutionGuard.Instance;
        _catalogOwner = TenantResolver.EffectiveDefaultOf(gatewayOptions?.Value.Tenancy);
        _gatewayOptions = gatewayOptions?.Value;
        _sqlExecutionService = sqlExecutionService ?? throw new ArgumentNullException(nameof(sqlExecutionService));
        _dataQueryService = dataQueryService ?? throw new ArgumentNullException(nameof(dataQueryService));
        _catalogDiscoveryService = catalogDiscoveryService ?? throw new ArgumentNullException(nameof(catalogDiscoveryService));
        _apiDispatcherService = apiDispatcherService ?? throw new ArgumentNullException(nameof(apiDispatcherService));
        _lineageGraphStore = lineageGraphStore ?? throw new ArgumentNullException(nameof(lineageGraphStore));
        _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
        _semanticCatalog = semanticCatalog;
        _metricQueryService = metricQueryService;
        _procedureCatalog = procedureCatalog;
        _procedureExecutionService = procedureExecutionService;
        _graphQlMap = graphQlMap;
        _searchEngine = searchEngine;
        _logger = logger ?? NullLogger<McpToolExecutionHandler>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool CanHandle(string toolName) =>
        string.Equals(toolName, McpDatasetTools.QuerySql, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.QueryDataset, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.SearchCatalog, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.FindRelevantTables, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.GetTableSchema, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.GetMyPermissions, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.ListDatasources, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.ListContainers, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.GetDataLineage, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.DescribeApi, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.InvokeApi, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.CallProcedure, StringComparison.OrdinalIgnoreCase) ||
        McpSemanticTools.IsSemanticTool(toolName);

    private const string QueryNextStep =
        "Call get_table_schema(datasets: [...]) to inspect required columns and join conditions, then generate query using query_sql or query_dataset.";

    /// <summary>
    /// Issue #804: the next step names the query tools only for candidates the caller can read; for the others it asks for an access
    /// request first. Built from the caller's own <c>accessStatus</c> values only; a candidate without a status keeps the query step.
    /// </summary>
    internal static string RecommendedNextStepFor(IReadOnlyList<McpUseCaseCandidate> candidates)
    {
        var notReadable = candidates.Count(static c =>
            c.AccessStatus is not null && !string.Equals(c.AccessStatus, CatalogAccessStatus.Granted, StringComparison.Ordinal));
        if (notReadable == 0)
        {
            return QueryNextStep;
        }

        return notReadable == candidates.Count
            ? "None of these tables is readable for you (accessStatus is not GRANTED): request access to the tables you need before querying them; describe_my_access explains your current access."
            : "Call get_table_schema(datasets: [...]) for the candidates with accessStatus GRANTED to inspect required columns and join conditions, then generate query using query_sql or query_dataset. Request access to the other candidates before querying them.";
    }

    /// <summary>Issue #804: the optional <c>accessStatus</c> argument; absent or null is no filter, any value that is not a known status is invalid.</summary>
    private static bool TryReadAccessFilter(JsonElement root, out DiscoveryAccessState? filter)
    {
        filter = null;
        if (!root.TryGetProperty("accessStatus", out var prop) || prop.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        return prop.ValueKind == JsonValueKind.String && CatalogAccessStatus.TryParseFilter(prop.GetString(), out filter);
    }

    /// <summary>
    /// Issue #447: the central tool error mapping. A transport denial from any gate call below becomes a tool error
    /// (<c>isError</c>) with the code <c>TRANSPORT_NOT_ALLOWED</c> and fixed text: no source name, no sub-reason.
    /// </summary>
    public async Task<string> ExecuteToolAsync(
        McpToolDefinition tool,
        string argumentsJson,
        McpSessionContext sessionContext,
        CancellationToken ct = default)
    {
        try
        {
            var userPrincipal = BuildUserPrincipal(sessionContext);
            return await _scopeGuard.RunAsync(userPrincipal, apiKey: null, sessionContext.ClientIp, (_, scopedCt) =>
                ExecuteToolCoreAsync(tool, argumentsJson, sessionContext, scopedCt), ct).ConfigureAwait(false);
        }
        catch (Autheris.Domain.Exceptions.ScopeConcurrencyExceededException)
        {
            return CreateMcpError(
                sessionContext.TenantId, tool.Name, "SCOPE_CONCURRENCY_EXCEEDED",
                "Too many concurrent queries are active for this client scope. Retry later.");
        }
        catch (TimeoutException)
        {
            return CreateMcpError(
                sessionContext.TenantId, tool.Name, "QUERY_TIMEOUT",
                "The query exceeded the maximum allowed execution duration.");
        }
        catch (TransportNotAllowedException)
        {
            return CreateMcpError(
                sessionContext.TenantId, tool.Name, TransportNotAllowedException.Code,
                "Access to the data source is not allowed over this transport.");
        }
    }

    private async Task<string> ExecuteToolCoreAsync(
        McpToolDefinition tool,
        string argumentsJson,
        McpSessionContext sessionContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(sessionContext);

        using var argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = argsDoc.RootElement;
        var userPrincipal = BuildUserPrincipal(sessionContext);
        var tenantId = new TenantId(sessionContext.TenantId);
        var roles = sessionContext.Roles ?? [];
        bool isGovAdmin = Autheris.Application.Security.GatewayRoleEvaluator.HasTenantBoundRole(
            McpProtocolHandler.BuildPrincipalFromSession(sessionContext, _timeProvider),
            nameof(GatewayRole.GovernanceAdmin),
            tenantId.Value);
        bool isClusterAdmin = roles.Any(Autheris.Domain.Security.ClusterAdminPolicy.IsClusterAdminRole);
        var groupsFresh = !string.IsNullOrWhiteSpace(sessionContext.UserSid)
            && !sessionContext.GroupsDegraded
            && (sessionContext.GroupsExpireAt is null || sessionContext.GroupsExpireAt > _timeProvider.GetUtcNow());
        var requestContext = RequestContext.FromCaller(
            new CallerSecurityContext(
                UserSid: new Sid(!string.IsNullOrWhiteSpace(sessionContext.UserSid) ? sessionContext.UserSid : sessionContext.ServicePrincipalId),
                // SR-M-08: an app-only session (no user SID) carries no group SIDs, and stale/degraded group snapshots carry none.
                GroupSids: groupsFresh ? (sessionContext.GroupSids ?? []).Select(g => new Sid(g)).ToArray() : [],
                Roles: roles,
                Tenant: tenantId,
                IsGovernanceAdmin: isGovAdmin,
                IsClusterAdmin: isClusterAdmin),
            sessionContext.SessionId);
        requestContext = requestContext with { User = userPrincipal };

        if (string.Equals(tool.Name, McpDatasetTools.QuerySql, StringComparison.OrdinalIgnoreCase))
        {
            var query = root.TryGetProperty("query", out var qProp) ? qProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(query))
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "Missing required argument 'query'.");
            }

            int? requestedLimit = null;
            if (root.TryGetProperty("limit", out var limitProp) && limitProp.ValueKind != JsonValueKind.Null)
            {
                if (limitProp.ValueKind != JsonValueKind.Number || !limitProp.TryGetInt32(out var parsedLimit) || parsedLimit < 1)
                {
                    return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "'limit' must be a positive integer.");
                }

                requestedLimit = parsedLimit;
            }

            string? cursor = null;
            if (root.TryGetProperty("cursor", out var cursorProp) && cursorProp.ValueKind != JsonValueKind.Null)
            {
                if (cursorProp.ValueKind != JsonValueKind.String)
                {
                    return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "Invalid continuation cursor.");
                }

                cursor = cursorProp.GetString();
                if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 512)
                {
                    return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "Invalid continuation cursor.");
                }
            }

            var effectiveLimit = McpRowCap.Effective(
                requestedLimit,
                McpRowCap.ResolveCap(_gatewayOptions?.Mcp?.MaxResultRows ?? McpRowCap.DefaultCap));
            if (_gatewayOptions?.WebSql?.MaxAllowedRows is > 0 and var maxAllowedRows && maxAllowedRows < effectiveLimit)
            {
                effectiveLimit = (int)maxAllowedRows;
            }
            bool hasSqlScope = Autheris.Application.Common.Quota.ScopeQueryExecutionGuard.TryGetCurrent(out var activeSqlScope);
            if (hasSqlScope && effectiveLimit > 0)
            {
                effectiveLimit = activeSqlScope.ClampRows(effectiveLimit);
            }

            var pageWindow = cursor is null || requestedLimit.HasValue
                ? new SqlPageWindow(0, effectiveLimit, 1)
                : null;
            var boundedRowLimit = Autheris.Application.Sql.SqlRowLimit.For(_gatewayOptions?.WebSql ?? new WebSqlOptions())
                .CappedAt(effectiveLimit);
            if (hasSqlScope && activeSqlScope.MaxResultRows is > 0)
            {
                boundedRowLimit = activeSqlScope.ClampRowLimit(boundedRowLimit);
            }

            try
            {
                GovernedSqlResult sqlResult;
                try
                {
                    sqlResult = await _sqlExecutionService.ExecuteQueryBufferedAsync(
                        new GovernedSqlQueryRequest(
                            query,
                            RowLimit: boundedRowLimit,
                            Transport: SqlTransports.Mcp,
                            Page: pageWindow,
                            NextToken: cursor),
                        userPrincipal,
                        tenantId,
                        ct).ConfigureAwait(false);
                }
                catch (SqlPagingUnsupportedException) when (cursor is null && requestedLimit is null)
                {
                    sqlResult = await _sqlExecutionService.ExecuteQueryBufferedAsync(
                        new GovernedSqlQueryRequest(query, RowLimit: boundedRowLimit, Transport: SqlTransports.Mcp),
                        userPrincipal,
                        tenantId,
                        ct).ConfigureAwait(false);
                }

                McpResultMasking.Report(sqlResult.HasPolicyMaskedValues);
                return JsonSerializer.Serialize(McpSqlQueryResult.From(sqlResult), JsonOpts);
            }
            catch (SqlPagingTokenInvalidException)
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "Invalid or expired continuation cursor.");
            }
            catch (SqlPagingUnsupportedException)
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "Cursor pagination is not supported for this query.");
            }
            catch (UnauthorizedAccessException)
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, ForbiddenCode, AccessDeniedMessage);
            }
        }

        if (string.Equals(tool.Name, McpDatasetTools.QueryDataset, StringComparison.OrdinalIgnoreCase))
        {
            var dataset = root.TryGetProperty("dataset", out var dsProp) ? dsProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(dataset) || !McpDatasetCatalog.TryParseDatasetId(dataset, out var tableId))
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "Missing or invalid required argument 'dataset'.");
            }

            IReadOnlyList<string>? select = null;
            if (root.TryGetProperty("select", out var selProp) && selProp.ValueKind == JsonValueKind.Array)
            {
                select = selProp.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
            }

            var filter = root.TryGetProperty("filter", out var fProp) ? fProp.GetString() : null;
            var orderBy = root.TryGetProperty("orderBy", out var oProp) ? oProp.GetString() : null;
            var limit = root.TryGetProperty("limit", out var lProp) && lProp.TryGetInt32(out var l) ? l : 50;
            if (Autheris.Application.Common.Quota.ScopeQueryExecutionGuard.TryGetCurrent(out var activeScope) && limit > 0)
            {
                limit = activeScope.ClampRows(limit);
            }
            var offset = root.TryGetProperty("offset", out var offProp) && offProp.TryGetInt32(out var off) ? off : 0;

            try
            {
                var datasetRequest = new DatasetQueryRequest(tableId, select, filter, orderBy, limit, offset, SqlTransports.Mcp);
                var result = await _dataQueryService.ExecuteQueryAsync(datasetRequest, requestContext, ct).ConfigureAwait(false);
                McpResultMasking.Report(result.Data.Count > 0 && result.Columns.Any(c => c.Masked));
                return JsonSerializer.Serialize(result, JsonOpts);
            }
            catch (UnauthorizedAccessException)
            {
                // Fixed text: the exception message can carry policy details (denied reasons, hidden table or column names).
                return CreateMcpError(sessionContext.TenantId, tool.Name, ForbiddenCode, AccessDeniedMessage);
            }
        }

        if (string.Equals(tool.Name, McpDatasetTools.SearchCatalog, StringComparison.OrdinalIgnoreCase))
        {
            var query = root.TryGetProperty("query", out var qProp) ? qProp.GetString() ?? "" : "";
            var domain = root.TryGetProperty("domain", out var dProp) ? dProp.GetString() : null;
            // The limit is caller-controlled: clamped to the documented maximum (default 5, max 20), like find_relevant_tables.
            var limit = root.TryGetProperty("limit", out var lProp) && lProp.TryGetInt32(out var l) ? Math.Clamp(l, 1, MaxCatalogSearchResults) : DefaultCatalogSearchResults;
            var modeStr = root.TryGetProperty("mode", out var mProp) ? mProp.GetString() : "hybrid";
            var language = root.TryGetProperty("language", out var langProp) ? langProp.GetString() : null;

            var mode = modeStr?.ToLowerInvariant() switch
            {
                "semantic" => CatalogSearchMode.SemanticVector,
                "keyword" => CatalogSearchMode.KeywordBm25,
                _ => CatalogSearchMode.Hybrid
            };

            var tagFilter = root.TryGetProperty("tag", out var tagProp) && tagProp.ValueKind == JsonValueKind.String ? tagProp.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(tagFilter))
            {
                tagFilter = null;
            }

            if (!TryReadAccessFilter(root, out var accessFilter))
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, CatalogAccessStatus.InvalidFilterMessage);
            }

            // A tag narrows the already authorized hits: the search is widened to the documented maximum, then cut back to the requested limit.
            // Issue #804: the access filter is applied by the search itself, before ranking and the limit.
            var searchQuery = new CatalogSearchQuery(query, domain, tagFilter is null ? limit : MaxCatalogSearchResults, mode, ExpandRelations: true, Language: language, AccessStatus: accessFilter);
            IReadOnlyList<CatalogSearchHit> results;
            try
            {
                results = await _catalogDiscoveryService.SearchCatalogDetailedAsync(searchQuery, requestContext, ct).ConfigureAwait(false);
                if (tagFilter is not null)
                {
                    results = await FilterHitsByTagAsync(results, tagFilter, limit, ct).ConfigureAwait(false);
                }
            }
            catch (GatewayInvalidQueryException)
            {
                // Fixed text (e.g. CATALOG_SEARCH_QUERY_TOO_LONG): the query is never echoed.
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, InvalidSearchQueryMessage);
            }

            var requestedMode = string.IsNullOrWhiteSpace(modeStr) ? "hybrid" : modeStr.Trim().ToLowerInvariant();
            var effectiveMode = results
                .Select(h => h.EffectiveMode)
                .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
                ?? _searchEngine?.ResolveEffectiveMode(mode)
                ?? "keyword";
            _logger.LogDebug(
                "Catalog search executed: requestedMode={RequestedMode}, effectiveMode={EffectiveMode}, hitCount={HitCount}.",
                requestedMode,
                effectiveMode,
                results.Count);

            var payloadItems = results.Select(h => new
            {
                table = McpDatasetCatalog.DatasetIdOf(h),
                sqlName = string.Equals(h.Kind, CatalogEntryKey.KindProcedure, StringComparison.Ordinal)
                    ? null
                    : McpDatasetCatalog.SqlNameOf(h.TableIdentifier, _gatewayOptions),
                domain = h.Domain,
                display_name = DiscoveryTextSanitizer.SanitizeText(h.DisplayName, DiscoveryTextSanitizer.MaxDisplayNameRunes),
                description = DiscoveryTextSanitizer.Summarize(h.Description),
                language = h.Language,
                relevance_score = Math.Round(h.CombinedScore, 4),
                accessStatus = h.AccessStatus,
                matched_terms = h.MatchedTerms.Select(t => DiscoveryTextSanitizer.SanitizeRequiredText(t, string.Empty, DiscoveryTextSanitizer.MaxDisplayNameRunes)).ToList(),
                matched_columns = h.RelevantColumns,
                columns = h.RelevantColumns.Select(c => new { name = c, sqlName = c }).ToList(),
                join_relations = h.RelatedJoinPaths.Select(r => new
                {
                    target_table = McpDatasetCatalog.DatasetId(r.ToTable),
                    direction = r.Incoming ? "incoming" : "outgoing",
                    join_condition = $"{h.TableIdentifier.TableName}.{r.FromColumn} = {r.ToTable.TableName}.{r.ToColumn}"
                })
            }).ToList();

            return SerializeBoundedList(payloadItems, items => new
            {
                contentNotice = McpDocTools.UntrustedContentNotice,
                total_hits = results.Count,
                query,
                mode = effectiveMode,
                datasets = items
            });
        }

        if (string.Equals(tool.Name, McpDatasetTools.FindRelevantTables, StringComparison.OrdinalIgnoreCase))
        {
            var useCase = root.TryGetProperty("use_case", out var ucProp) ? ucProp.GetString() ?? "" :
                          (root.TryGetProperty("query", out var qProp) ? qProp.GetString() ?? "" : "");
            var domain = root.TryGetProperty("domain", out var dProp) ? dProp.GetString() : null;
            var limit = root.TryGetProperty("limit", out var lProp) && lProp.TryGetInt32(out var l) ? Math.Clamp(l, 1, MaxCatalogSearchResults) : DefaultCatalogSearchResults;
            var language = root.TryGetProperty("language", out var langProp) ? langProp.GetString() : null;
            if (!TryReadAccessFilter(root, out var accessFilter))
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, CatalogAccessStatus.InvalidFilterMessage);
            }

            var searchQuery = new CatalogSearchQuery(useCase, domain, limit, CatalogSearchMode.Hybrid, ExpandRelations: true, Language: language, AccessStatus: accessFilter);
            IReadOnlyList<CatalogSearchHit> results;
            try
            {
                results = await _catalogDiscoveryService.SearchCatalogDetailedAsync(searchQuery, requestContext, ct).ConfigureAwait(false);
            }
            catch (GatewayInvalidQueryException)
            {
                // Fixed text (e.g. CATALOG_SEARCH_QUERY_TOO_LONG): the use case is never echoed.
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, InvalidSearchQueryMessage);
            }

            var candidates = results.Select(h => new McpUseCaseCandidate(
                Dataset: McpDatasetCatalog.DatasetIdOf(h),
                Domain: h.Domain ?? h.TableIdentifier.Domain ?? "",
                Description: DiscoveryTextSanitizer.SanitizeText(h.Description, DiscoveryTextSanitizer.MaxDescriptionRunes),
                RelevanceScore: Math.Round(h.CombinedScore, 4),
                MatchedColumns: h.RelevantColumns,
                Relations: h.RelatedJoinPaths.Select(r => new McpTableRelation(
                    TargetTable: McpDatasetCatalog.DatasetId(r.ToTable),
                    FromColumn: r.FromColumn,
                    ToColumn: r.ToColumn)).ToList(),
                AccessStatus: h.AccessStatus
            )).ToList();

            return SerializeBoundedList(candidates, items => new
            {
                contentNotice = McpDocTools.UntrustedContentNotice,
                UseCase = useCase,
                TotalCandidates = results.Count,
                Candidates = items,
                RecommendedNextStep = RecommendedNextStepFor(candidates)
            });
        }

        if (string.Equals(tool.Name, McpDatasetTools.GetTableSchema, StringComparison.OrdinalIgnoreCase))
        {
            // Same parser as the guardrail's ABAC targets (McpDatasetTools.TableSchemaTargets): only the tables that were checked are read.
            if (!McpDatasetTools.TryGetTableSchemaDatasets(argumentsJson, out var requestedTables))
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "Invalid argument 'dataset' or 'datasets'.");
            }

            if (requestedTables.Count == 0)
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "Missing required argument 'dataset' or 'datasets'.");
            }

            // Default true, as the input schema documents; only a JSON false turns relations off.
            var includeRelations = !(root.TryGetProperty("include_relations", out var irProp) && irProp.ValueKind == JsonValueKind.False);
            var visibleDetails = new Dictionary<TableIdentifier, CatalogDatasetDetail?>();
            var briefSchemas = new List<McpTableBriefSchema>();
            foreach (var tableId in requestedTables)
            {
                if (!visibleDetails.TryGetValue(tableId, out var detail))
                {
                    detail = await _catalogDiscoveryService.GetDatasetDetailAsync(tableId, requestContext, ct).ConfigureAwait(false);
                    visibleDetails[tableId] = detail;
                }

                if (detail == null)
                {
                    continue;
                }

                // Root field of the table and field of each visible column in the catalog GraphQL schema; only columns of the caller's own view are emitted.
                var graphQlMapping = _graphQlMap is null ? null : await _graphQlMap.GetTableAsync(tableId, ct).ConfigureAwait(false);
                var graphQlFields = graphQlMapping?.Columns.ToDictionary(m => m.ColumnName, m => m.Field, StringComparer.OrdinalIgnoreCase);

                var columns = DiscoveryColumnVisibility.VisibleColumns(detail).Select(c => new McpTableBriefColumn(
                    Name: c.Name,
                    Type: c.Type,
                    Description: c.Description,
                    PrimaryKey: c.IsPrimaryKey,
                    IsSensitive: !string.IsNullOrEmpty(c.Sensitivity) && !string.Equals(c.Sensitivity, "Public", StringComparison.OrdinalIgnoreCase) && !string.Equals(c.Sensitivity, "NORMAL", StringComparison.OrdinalIgnoreCase),
                    GraphQlField: graphQlFields is not null && graphQlFields.TryGetValue(c.Name, out var columnField) ? columnField : null,
                    // The caller's own state, from the same filtered detail row as the column list: clear, or mask for any masking rule.
                    Access: c.MaskingState switch
                    {
                        null or "" => null,
                        var state when string.Equals(state, "Clear", StringComparison.OrdinalIgnoreCase) => "clear",
                        var state when string.Equals(state, "Deny", StringComparison.OrdinalIgnoreCase) => null,
                        _ => "mask"
                    }
                )).ToList();

                // MCP-05: only declared relations are emitted, never guessed from column names.
                var relations = includeRelations
                    ? await DeclaredRelationsAsync(tableId, detail, visibleDetails, requestContext, ct).ConfigureAwait(false)
                    : [];

                briefSchemas.Add(new McpTableBriefSchema(
                    Dataset: detail.DatasetId,
                    Domain: detail.Domain,
                    Description: detail.Description,
                    PrimaryKeys: detail.PrimaryKeys,
                    Columns: columns,
                    Relations: relations,
                    GraphQlField: graphQlMapping?.QueryField));
            }

            return JsonSerializer.Serialize(new McpTableSchemaResult(briefSchemas.Count, briefSchemas), JsonOpts);
        }

        if (string.Equals(tool.Name, McpDatasetTools.GetMyPermissions, StringComparison.OrdinalIgnoreCase))
        {
            var dataset = root.TryGetProperty("dataset", out var dsProp) ? dsProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(dataset) || !McpDatasetCatalog.TryParseDatasetId(dataset, out var tableId))
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "Missing or invalid required argument 'dataset'.");
            }

            var detail = await _catalogDiscoveryService.GetDatasetDetailAsync(tableId, requestContext, ct).ConfigureAwait(false);
            if (detail == null)
            {
                return JsonSerializer.Serialize(new { dataset, allowed = false, message = "Access denied or dataset not found." }, JsonOpts);
            }

            return JsonSerializer.Serialize(new
            {
                dataset = detail.DatasetId,
                allowed = true,
                isActive = detail.IsActive,
                sensitivity = detail.Sensitivity,
                columns = DiscoveryColumnVisibility.VisibleColumns(detail).Select(c => new
                {
                    name = c.Name,
                    type = c.Type,
                    masking = c.MaskingState,
                    isPrimaryKey = c.IsPrimaryKey,
                    isPii = c.IsPiiIndicator
                })
            }, JsonOpts);
        }

        if (string.Equals(tool.Name, McpDatasetTools.ListDatasources, StringComparison.OrdinalIgnoreCase))
        {
            // Data sources disclose endpoint URLs and authentication state: administrator view only (canonical caller class).
            var datasourceScope = CallerScopeResolver.Resolve(userPrincipal, _catalogOwner);
            if (!CallerScopeResolver.IsAdminView(datasourceScope))
            {
                return CreateMcpError(
                    sessionContext.TenantId,
                    tool.Name,
                    ForbiddenCode,
                    CallerScopeResolver.DatasourceListingDenial(userPrincipal, datasourceScope));
            }

            var status = root.TryGetProperty("status", out var sProp) ? sProp.GetString() : null;
            var datasources = await _catalogDiscoveryService.ListDatasourcesAsync(requestContext, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(status))
            {
                datasources = datasources.Where(d => string.Equals(d.Status, status, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            return JsonSerializer.Serialize(datasources, JsonOpts);
        }

        if (string.Equals(tool.Name, McpDatasetTools.ListContainers, StringComparison.OrdinalIgnoreCase))
        {
            return await McpContainerCatalog.ListAsync(_containerCatalog, tenantId, userPrincipal, root, ct).ConfigureAwait(false);
        }

        if (string.Equals(tool.Name, McpDatasetTools.GetDataLineage, StringComparison.OrdinalIgnoreCase))
        {
            var dataset = root.TryGetProperty("dataset", out var dsProp) ? dsProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(dataset))
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "Missing required argument 'dataset'.");
            }

            var node = _lineageGraphStore.GetNode(dataset);
            bool isTenantMatch = node != null && (isClusterAdmin || string.IsNullOrWhiteSpace(node.TenantId) || string.Equals(node.TenantId, sessionContext.TenantId, StringComparison.OrdinalIgnoreCase));

            // The node must also be a catalog dataset the caller may see (same visibility as get_table_schema/get_my_permissions), so a
            // tenant-less node is no longer enough on its own. Unknown, foreign and hidden nodes get one indistinguishable answer.
            if (node == null || !isTenantMatch || !await IsLineageNodeVisibleAsync(node.Id, requestContext, ct).ConfigureAwait(false))
            {
                return JsonSerializer.Serialize(new
                {
                    dataset,
                    hasLineage = false,
                    message = $"No lineage graph found for '{dataset}'."
                }, JsonOpts);
            }

            // Downstream consumers the caller cannot see are left out (their ids alone disclose the existence of hidden datasets).
            var visibleDownstream = new List<string>();
            foreach (var downstreamId in node.DownstreamNodeIds)
            {
                if (await IsLineageNodeVisibleAsync(downstreamId, requestContext, ct).ConfigureAwait(false))
                {
                    visibleDownstream.Add(downstreamId);
                }
            }

            return JsonSerializer.Serialize(new
            {
                dataset = node.Id,
                name = node.Name,
                type = node.Type.ToString(),
                downstreamNodes = visibleDownstream,
                ownerTeam = node.OwnerTeam,
                hasLineage = true
            }, JsonOpts);
        }

        if (string.Equals(tool.Name, McpDatasetTools.DescribeApi, StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = root.TryGetProperty("endpoint", out var epProp) ? epProp.GetString() ?? "" : "";
            var method = root.TryGetProperty("method", out var mProp) ? mProp.GetString() : null;

            return await _apiDispatcherService.DescribeApiAsync(endpoint, method, ct).ConfigureAwait(false);
        }

        if (string.Equals(tool.Name, McpDatasetTools.InvokeApi, StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = root.TryGetProperty("endpoint", out var epProp) ? epProp.GetString() ?? "" : "";
            var method = root.TryGetProperty("method", out var mProp) ? mProp.GetString() ?? "GET" : "GET";

            Dictionary<string, object?>? parameters = null;
            if (root.TryGetProperty("parameters", out var pProp) && pProp.ValueKind == JsonValueKind.Object)
            {
                parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in pProp.EnumerateObject())
                {
                    parameters[prop.Name] = prop.Value.ToString();
                }
            }

            object? body = root.TryGetProperty("body", out var bProp) ? bProp : null;
            return await _apiDispatcherService.InvokeApiAsync(endpoint, method, parameters, body, requestContext, ct).ConfigureAwait(false);
        }

        if (string.Equals(tool.Name, McpSemanticTools.ListMetrics, StringComparison.OrdinalIgnoreCase))
        {
            return await McpSemanticTools.ExecuteListMetricsAsync(
                _semanticCatalog ?? new Autheris.Application.Catalog.Semantic.InMemorySemanticCatalog(),
                userPrincipal,
                new Autheris.Application.Catalog.Semantic.SemanticTenancy(_catalogOwner),
                argumentsJson,
                ct).ConfigureAwait(false);
        }

        if (string.Equals(tool.Name, McpSemanticTools.DescribeMetric, StringComparison.OrdinalIgnoreCase))
        {
            return await McpSemanticTools.ExecuteDescribeMetricAsync(
                _semanticCatalog ?? new Autheris.Application.Catalog.Semantic.InMemorySemanticCatalog(),
                userPrincipal,
                new Autheris.Application.Catalog.Semantic.SemanticTenancy(_catalogOwner),
                argumentsJson,
                ct).ConfigureAwait(false);
        }

        if (string.Equals(tool.Name, McpSemanticTools.QueryMetrics, StringComparison.OrdinalIgnoreCase))
        {
            if (_metricQueryService == null)
            {
                return McpToolError.Serialize("NOT_AVAILABLE", "Semantic metric query service is not configured.");
            }

            return await McpSemanticTools.ExecuteQueryMetricsAsync(_metricQueryService, userPrincipal, argumentsJson, ct).ConfigureAwait(false);
        }

        if (string.Equals(tool.Name, McpDatasetTools.CallProcedure, StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteCallProcedureAsync(root, userPrincipal, sessionContext, tenantId, ct).ConfigureAwait(false);
        }

        throw new NotSupportedException($"Tool '{tool.Name}' is not supported by McpToolExecutionHandler.");
    }

    private async Task<string> ExecuteCallProcedureAsync(
        JsonElement root,
        ClaimsPrincipal userPrincipal,
        McpSessionContext sessionContext,
        TenantId tenantId,
        CancellationToken ct)
    {
        // without the procedure services (kill switch off) the tool is answered like an unknown one, before any
        // argument is looked at; never NOT_AVAILABLE, which would reveal the feature.
        if (_procedureCatalog == null || _procedureExecutionService == null)
        {
            return CreateMcpError(sessionContext.TenantId, McpDatasetTools.CallProcedure, "NOT_FOUND",
                $"Tool '{McpDatasetTools.CallProcedure}' is not registered or allowed.");
        }

        // Same parsing as the guardrail's ABAC and four-eyes targets, so the checked procedure is the one that runs.
        var procName = McpDatasetTools.ProcedureNameArgument(root.GetRawText());
        if (procName == null)
        {
            return CreateMcpError(sessionContext.TenantId, McpDatasetTools.CallProcedure, "INVALID_PARAMS", "Missing required argument 'procedure_name'.");
        }

        var rawInputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in argsProp.EnumerateObject())
            {
                rawInputs[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.ToString()
                };
            }
        }

        int maxRows = 100;
        if (root.TryGetProperty("max_rows", out var mrProp) && mrProp.TryGetInt32(out var mr))
        {
            maxRows = Math.Clamp(mr, 1, 1000);
        }
        rawInputs["limit"] = maxRows;

        bool isReadOnlyCaller = userPrincipal.IsReadOnly() || sessionContext.IsReadOnly;
        // Unknown and invisible procedures both fall through to the executor, which answers and audits them alike.
        var reg = await _procedureCatalog.FindVisibleAsync(procName, userPrincipal, tenantId, ct).ConfigureAwait(false);
        if (reg != null && isReadOnlyCaller && reg.Definition.Mode != ProcedureMode.Read)
        {
            return CreateMcpError(sessionContext.TenantId, McpDatasetTools.CallProcedure, "FORBIDDEN", "This token only permits read access; write procedures are rejected.");
        }

        try
        {
            using var callScope = Autheris.Application.Procedures.Interfaces.ProcedureCallScope.Enter(Autheris.Application.Procedures.Interfaces.ProcedureCallTransport.Mcp);
            var result = await _procedureExecutionService.ExecuteAsync(procName, rawInputs, userPrincipal, tenantId, ct).ConfigureAwait(false);

            McpResultMasking.Report(result.HasMaskedValues && result.Rows.Count > 0);
            var rows = result.Rows;
            bool truncated = result.Truncated || rows.Count > maxRows;
            if (rows.Count > maxRows)
            {
                rows = rows.Take(maxRows).ToList();
            }

            return JsonSerializer.Serialize(new
            {
                columns = result.Columns,
                rows = rows,
                rowCount = rows.Count,
                truncated = truncated
            }, JsonOpts);
        }
        catch (KeyNotFoundException)
        {
            return CreateMcpError(sessionContext.TenantId, McpDatasetTools.CallProcedure, "NOT_FOUND", "Procedure endpoint not found.");
        }
        catch (Autheris.Application.Procedures.Interfaces.ProcedureBusinessException ex)
        {
            return CreateMcpError(sessionContext.TenantId, McpDatasetTools.CallProcedure, "EXECUTION_FAILED", ex.MappedMessage ?? ex.Message);
        }
        catch (Autheris.Application.Procedures.Interfaces.ProcedureUnavailableException)
        {
            return CreateMcpError(sessionContext.TenantId, McpDatasetTools.CallProcedure, "NOT_AVAILABLE", "Procedure endpoint is currently unavailable.");
        }
        catch (GatewayForbiddenException)
        {
            return CreateMcpError(sessionContext.TenantId, McpDatasetTools.CallProcedure, "FORBIDDEN", "Access denied by data governance policy.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TransportNotAllowedException and not TimeoutException)
        {
            return CreateMcpError(sessionContext.TenantId, McpDatasetTools.CallProcedure, "EXECUTION_FAILED", "Procedure execution failed.");
        }
    }

    /// <summary>
    /// Whether a lineage node id names a catalog dataset the caller may see. Ids that are no table identifier (dashboards, pipelines,
    /// malformed ids) cannot be checked against the catalog and are not visible (fail closed).
    /// </summary>
    private async Task<bool> IsLineageNodeVisibleAsync(string? nodeId, RequestContext requestContext, CancellationToken ct)
    {
        TableIdentifier tableId;
        try
        {
            if (!TableIdentifier.TryParse(nodeId, out tableId))
            {
                return false;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        var detail = await _catalogDiscoveryService.GetDatasetDetailAsync(tableId, requestContext, ct).ConfigureAwait(false);
        return detail != null;
    }

    /// <summary>
    /// Issue #711: the declared relations of a table (catalog GraphQL schema) as single column pairs. Like <c>describe_dataset</c>
    /// (issue #392) a relation is listed only towards a table the caller can see and only when its join columns are listed for the
    /// caller on both sides. A composite key is not listed: one entry carries one column pair.
    /// </summary>
    private async Task<IReadOnlyList<McpTableRelation>> DeclaredRelationsAsync(
        TableIdentifier table,
        CatalogDatasetDetail detail,
        Dictionary<TableIdentifier, CatalogDatasetDetail?> visibleDetails,
        RequestContext requestContext,
        CancellationToken ct)
    {
        if (_graphQlMap == null || DiscoveryColumnVisibility.VisibleColumns(detail).Count == 0)
        {
            return [];
        }

        var mapping = await _graphQlMap.GetTableAsync(table, ct).ConfigureAwait(false);
        if (mapping == null)
        {
            return [];
        }

        var relations = new List<McpTableRelation>();
        foreach (var relation in mapping.Relations)
        {
            if (relation.Columns is not [var fromColumn] || relation.TargetColumns is not [var toColumn] || !Lists(detail, fromColumn))
            {
                continue;
            }

            if (!visibleDetails.TryGetValue(relation.Target, out var target))
            {
                target = await _catalogDiscoveryService.GetDatasetDetailAsync(relation.Target, requestContext, ct).ConfigureAwait(false);
                visibleDetails[relation.Target] = target;
            }

            if (target != null && Lists(target, toColumn))
            {
                relations.Add(new McpTableRelation(McpDatasetCatalog.DatasetId(relation.Target), fromColumn, toColumn));
            }
        }

        return relations;

        static bool Lists(CatalogDatasetDetail dataset, string column) =>
            DiscoveryColumnVisibility.VisibleColumns(dataset).Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<CatalogSearchHit>> FilterHitsByTagAsync(IReadOnlyList<CatalogSearchHit> hits, string tag, int limit, CancellationToken ct)
    {
        var kept = new List<CatalogSearchHit>(Math.Min(limit, hits.Count));
        foreach (var hit in hits)
        {
            if (kept.Count >= limit)
            {
                break;
            }

            if (!string.Equals(hit.Kind, "table", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var metadata = await _metadataRepo.GetTableMetadataAsync(hit.TableIdentifier, ct).ConfigureAwait(false);
            if (metadata is not null && metadata.CatalogTags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
            {
                kept.Add(hit);
            }
        }

        return kept;
    }

    private static string CreateMcpError(string tenantId, string tool, string code, string message) =>
        McpToolError.Serialize(code, message, tenantId, tool);

    private static string SerializeBoundedList<T>(IReadOnlyList<T> items, Func<IReadOnlyList<T>, object> envelope)
    {
        var slice = items;
        while (slice.Count > 0)
        {
            var json = JsonSerializer.Serialize(envelope(slice), JsonOpts);
            if (System.Text.Encoding.UTF8.GetByteCount(json) <= DiscoveryTextSanitizer.MaxMcpPayloadBytes)
            {
                return json;
            }

            var nextCount = slice.Count == 1 ? 0 : slice.Count / 2;
            slice = slice.Take(nextCount).ToList();
        }

        return JsonSerializer.Serialize(envelope([]), JsonOpts);
    }

    private ClaimsPrincipal BuildUserPrincipal(McpSessionContext session)
    {
        // SR-M-08: same principal as the other MCP paths (user SID, groups, claims, read-only marker); app-only sessions stay marked as such.
        var principal = McpProtocolHandler.BuildPrincipalFromSession(session, _timeProvider)!;
        if (!string.IsNullOrWhiteSpace(session.TenantId) && principal.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim("tid", session.TenantId));
        }

        return principal;
    }

    private sealed record McpSqlQueryResult(
        string OriginalSql,
        IReadOnlyList<string> Columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
        int RowCount,
        long ElapsedMilliseconds,
        bool Truncated,
        bool HasMore,
        string? Cursor,
        GovernedSqlPageResponse? Page,
        string? Next,
        string? PagingUnavailable,
        IReadOnlyList<WebSqlIndexHint>? IndexHints)
    {
        public static McpSqlQueryResult From(GovernedSqlResult result) => new(
            OriginalSql: result.OriginalSql,
            Columns: result.Columns,
            Rows: result.Rows,
            RowCount: result.RowCount,
            ElapsedMilliseconds: result.ElapsedMilliseconds,
            Truncated: result.Truncated,
            HasMore: result.Next is not null || result.Truncated,
            Cursor: result.Next,
            Page: result.Page,
            Next: result.Next,
            PagingUnavailable: result.PagingUnavailable,
            IndexHints: result.IndexHints);
    }
}
