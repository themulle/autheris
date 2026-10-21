namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Pipeline handler for executing MCP tools without bloating AiDataGuardrailService or GatewayMcpQueryExecutor.
/// </summary>
public sealed class McpToolExecutionHandler : IMcpToolExecutionHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IGovernedSqlExecutionService _sqlExecutionService;
    private readonly IGovernedDataQueryService _dataQueryService;
    private readonly ICatalogDiscoveryService _catalogDiscoveryService;
    private readonly IApiDispatcherService _apiDispatcherService;
    private readonly ILineageGraphStore _lineageGraphStore;
    private readonly ITableMetadataRepository _metadataRepo;
    private readonly ISemanticCatalogService? _semanticCatalog;
    private readonly Autheris.Application.Analytics.ISemanticMetricQueryService? _metricQueryService;
    private readonly TenancyMode _tenancyMode;
    private readonly Autheris.Application.Procedures.Interfaces.IProcedureRegistry? _procedureRegistry;
    private readonly Autheris.Application.Procedures.Interfaces.IProcedureExecutionService? _procedureExecutionService;

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
        Autheris.Application.Procedures.Interfaces.IProcedureRegistry? procedureRegistry = null,
        Autheris.Application.Procedures.Interfaces.IProcedureExecutionService? procedureExecutionService = null)
    {
        _tenancyMode = gatewayOptions?.Value.Tenancy.EffectiveMode ?? TenancyMode.Multi;
        _sqlExecutionService = sqlExecutionService ?? throw new ArgumentNullException(nameof(sqlExecutionService));
        _dataQueryService = dataQueryService ?? throw new ArgumentNullException(nameof(dataQueryService));
        _catalogDiscoveryService = catalogDiscoveryService ?? throw new ArgumentNullException(nameof(catalogDiscoveryService));
        _apiDispatcherService = apiDispatcherService ?? throw new ArgumentNullException(nameof(apiDispatcherService));
        _lineageGraphStore = lineageGraphStore ?? throw new ArgumentNullException(nameof(lineageGraphStore));
        _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
        _semanticCatalog = semanticCatalog;
        _metricQueryService = metricQueryService;
        _procedureRegistry = procedureRegistry;
        _procedureExecutionService = procedureExecutionService;
    }

    public bool CanHandle(string toolName) =>
        string.Equals(toolName, McpDatasetTools.QuerySql, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.QueryDataset, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.SearchCatalog, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.GetMyPermissions, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.ListDatasources, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.GetDataLineage, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.DescribeApi, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.InvokeApi, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.CallProcedure, StringComparison.OrdinalIgnoreCase) ||
        McpSemanticTools.IsSemanticTool(toolName);

    public async Task<string> ExecuteToolAsync(
        McpToolDefinition tool,
        string argumentsJson,
        McpSessionContext sessionContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(sessionContext);

        var argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = argsDoc.RootElement;
        var userPrincipal = BuildUserPrincipal(sessionContext);
        var tenantId = new TenantId(sessionContext.TenantId);
        var roles = sessionContext.Roles ?? [];
        bool isGovAdmin = roles.Contains("GovernanceAdmin", StringComparer.OrdinalIgnoreCase);
        bool isClusterAdmin = roles.Contains("ClusterAdmin", StringComparer.OrdinalIgnoreCase);
        var requestContext = RequestContext.FromCaller(
            new CallerSecurityContext(
                UserSid: new Sid(sessionContext.UserSid ?? sessionContext.ServicePrincipalId),
                GroupSids: (sessionContext.GroupSids ?? []).Select(g => new Sid(g)).ToArray(),
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
                return JsonSerializer.Serialize(new { error = "InvalidParams", message = "Missing required argument 'query'." }, JsonOpts);
            }

            var sqlResult = await _sqlExecutionService.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest(query, Transport: SqlTransports.Mcp),
                userPrincipal,
                tenantId,
                ct).ConfigureAwait(false);

            return JsonSerializer.Serialize(sqlResult, JsonOpts);
        }

        if (string.Equals(tool.Name, McpDatasetTools.QueryDataset, StringComparison.OrdinalIgnoreCase))
        {
            var dataset = root.TryGetProperty("dataset", out var dsProp) ? dsProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(dataset) || !McpDatasetCatalog.TryParseDatasetId(dataset, out var tableId))
            {
                return JsonSerializer.Serialize(new { error = "InvalidParams", message = "Missing or invalid required argument 'dataset'." }, JsonOpts);
            }

            IReadOnlyList<string>? select = null;
            if (root.TryGetProperty("select", out var selProp) && selProp.ValueKind == JsonValueKind.Array)
            {
                select = selProp.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
            }

            var filter = root.TryGetProperty("filter", out var fProp) ? fProp.GetString() : null;
            var orderBy = root.TryGetProperty("orderBy", out var oProp) ? oProp.GetString() : null;
            var limit = root.TryGetProperty("limit", out var lProp) && lProp.TryGetInt32(out var l) ? l : 50;
            var offset = root.TryGetProperty("offset", out var offProp) && offProp.TryGetInt32(out var off) ? off : 0;

            try
            {
                var datasetRequest = new DatasetQueryRequest(tableId, select, filter, orderBy, limit, offset, SqlTransports.Mcp);
                var result = await _dataQueryService.ExecuteQueryAsync(datasetRequest, requestContext, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(result, JsonOpts);
            }
            catch (UnauthorizedAccessException ex)
            {
                return JsonSerializer.Serialize(new { error = "Unauthorized", message = ex.Message }, JsonOpts);
            }
        }

        if (string.Equals(tool.Name, McpDatasetTools.SearchCatalog, StringComparison.OrdinalIgnoreCase))
        {
            var query = root.TryGetProperty("query", out var qProp) ? qProp.GetString() ?? "" : "";
            var domain = root.TryGetProperty("domain", out var dProp) ? dProp.GetString() : null;
            var limit = root.TryGetProperty("limit", out var lProp) && lProp.TryGetInt32(out var l) ? l : 5;
            var modeStr = root.TryGetProperty("mode", out var mProp) ? mProp.GetString() : "hybrid";
            var language = root.TryGetProperty("language", out var langProp) ? langProp.GetString() : null;

            var mode = modeStr?.ToLowerInvariant() switch
            {
                "semantic" => CatalogSearchMode.SemanticVector,
                "keyword" => CatalogSearchMode.KeywordBm25,
                _ => CatalogSearchMode.Hybrid
            };

            var searchQuery = new CatalogSearchQuery(query, domain, limit, mode, ExpandRelations: true, Language: language);
            var results = await _catalogDiscoveryService.SearchCatalogDetailedAsync(searchQuery, requestContext, ct).ConfigureAwait(false);

            return JsonSerializer.Serialize(new
            {
                total_hits = results.Count,
                query,
                datasets = results.Select(h => new
                {
                    table = $"{h.TableIdentifier.Domain}.{h.TableIdentifier.TableName}",
                    domain = h.Domain,
                    display_name = h.DisplayName,
                    description = h.Description,
                    language = h.Language,
                    relevance_score = Math.Round(h.CombinedScore, 4),
                    matched_terms = h.MatchedTerms,
                    matched_columns = h.RelevantColumns,
                    join_relations = h.RelatedJoinPaths.Select(r => new
                    {
                        target_table = $"{r.ToTable.Domain}.{r.ToTable.TableName}",
                        join_condition = $"{h.TableIdentifier.TableName}.{r.FromColumn} = {r.ToTable.TableName}.{r.ToColumn}"
                    })
                })
            }, JsonOpts);
        }

        if (string.Equals(tool.Name, McpDatasetTools.GetMyPermissions, StringComparison.OrdinalIgnoreCase))
        {
            var dataset = root.TryGetProperty("dataset", out var dsProp) ? dsProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(dataset) || !McpDatasetCatalog.TryParseDatasetId(dataset, out var tableId))
            {
                return JsonSerializer.Serialize(new { error = "InvalidParams", message = "Missing or invalid required argument 'dataset'." }, JsonOpts);
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
                columns = detail.Columns.Select(c => new
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
            var datasourceScope = CallerScopeResolver.Resolve(userPrincipal, _tenancyMode);
            if (!CallerScopeResolver.IsAdminView(datasourceScope))
            {
                return JsonSerializer.Serialize(new
                {
                    error = "Forbidden",
                    message = datasourceScope == CallerScope.Claimless &&
                              CallerScopeResolver.HoldsGovernanceAdminRole(userPrincipal)
                        ? "Listing data sources requires an administrator with a tenant claim; single-tenant deployments must set Gateway:Tenancy:Mode=Single."
                        : "Listing data sources requires a governance administrator."
                }, JsonOpts);
            }

            var status = root.TryGetProperty("status", out var sProp) ? sProp.GetString() : null;
            var datasources = await _catalogDiscoveryService.ListDatasourcesAsync(requestContext, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(status))
            {
                datasources = datasources.Where(d => string.Equals(d.Status, status, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            return JsonSerializer.Serialize(datasources, JsonOpts);
        }

        if (string.Equals(tool.Name, McpDatasetTools.GetDataLineage, StringComparison.OrdinalIgnoreCase))
        {
            var dataset = root.TryGetProperty("dataset", out var dsProp) ? dsProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(dataset))
            {
                return JsonSerializer.Serialize(new { error = "InvalidParams", message = "Missing required argument 'dataset'." }, JsonOpts);
            }

            var node = _lineageGraphStore.GetNode(dataset);
            bool isTenantMatch = node != null && (isClusterAdmin || string.IsNullOrWhiteSpace(node.TenantId) || string.Equals(node.TenantId, sessionContext.TenantId, StringComparison.OrdinalIgnoreCase));

            if (node == null || !isTenantMatch)
            {
                return JsonSerializer.Serialize(new
                {
                    dataset,
                    hasLineage = false,
                    message = $"No lineage graph found for '{dataset}'."
                }, JsonOpts);
            }

            return JsonSerializer.Serialize(new
            {
                dataset = node.Id,
                name = node.Name,
                type = node.Type.ToString(),
                downstreamNodes = node.DownstreamNodeIds,
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
                _semanticCatalog ?? new Autheris.Application.Dbt.Services.InMemorySemanticCatalog(),
                userPrincipal,
                new Autheris.Application.Dbt.Services.SemanticTenancy(_tenancyMode),
                argumentsJson,
                ct).ConfigureAwait(false);
        }

        if (string.Equals(tool.Name, McpSemanticTools.DescribeMetric, StringComparison.OrdinalIgnoreCase))
        {
            return await McpSemanticTools.ExecuteDescribeMetricAsync(
                _semanticCatalog ?? new Autheris.Application.Dbt.Services.InMemorySemanticCatalog(),
                userPrincipal,
                new Autheris.Application.Dbt.Services.SemanticTenancy(_tenancyMode),
                argumentsJson,
                ct).ConfigureAwait(false);
        }

        if (string.Equals(tool.Name, McpSemanticTools.QueryMetrics, StringComparison.OrdinalIgnoreCase))
        {
            if (_metricQueryService == null)
            {
                return JsonSerializer.Serialize(new { isError = true, error = new { code = "NOT_AVAILABLE", message = "Semantic metric query service is not configured." } }, JsonOpts);
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
        var procNameRaw = root.TryGetProperty("procedure_name", out var pnProp) ? pnProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(procNameRaw))
        {
            return CreateMcpError(sessionContext.TenantId, McpDatasetTools.CallProcedure, "INVALID_PARAMS", "Missing required argument 'procedure_name'.");
        }

        var procName = procNameRaw.StartsWith("procedure:", StringComparison.OrdinalIgnoreCase)
            ? procNameRaw["procedure:".Length..].Trim()
            : procNameRaw.Trim();

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

        if (_procedureRegistry == null || _procedureExecutionService == null)
        {
            return CreateMcpError(sessionContext.TenantId, McpDatasetTools.CallProcedure, "NOT_AVAILABLE", "Procedure execution service is not configured.");
        }

        bool isReadOnlyCaller = userPrincipal.IsReadOnly() || sessionContext.IsReadOnly;
        if (_procedureRegistry.TryGet(procName, out var reg) && reg != null)
        {
            // Visibility check first: prevents existence and mode oracle (SEC-GOV153-02)
            var isVisible = await _procedureExecutionService.IsVisibleAsync(reg, userPrincipal, tenantId, ct).ConfigureAwait(false);
            if (!isVisible)
            {
                return CreateMcpError(sessionContext.TenantId, McpDatasetTools.CallProcedure, "NOT_FOUND", "Procedure endpoint not found.");
            }

            if (isReadOnlyCaller && reg.Definition.Mode != ProcedureMode.Read)
            {
                return CreateMcpError(sessionContext.TenantId, McpDatasetTools.CallProcedure, "FORBIDDEN", "This token only permits read access; write procedures are rejected.");
            }
        }

        try
        {
            var result = await _procedureExecutionService.ExecuteAsync(procName, rawInputs, userPrincipal, tenantId, ct).ConfigureAwait(false);

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateMcpError(sessionContext.TenantId, McpDatasetTools.CallProcedure, "EXECUTION_FAILED", "Procedure execution failed.");
        }
    }

    private static string CreateMcpError(string tenantId, string tool, string code, string message)
    {
        return JsonSerializer.Serialize(new
        {
            tenantId,
            tool,
            isError = true,
            error = new
            {
                code,
                message
            }
        }, JsonOpts);
    }

    private static ClaimsPrincipal BuildUserPrincipal(McpSessionContext session)
    {
        // SR-M-08: same principal as the other MCP paths (user SID, groups, claims, read-only marker); app-only sessions stay marked as such.
        var principal = McpProtocolHandler.BuildPrincipalFromSession(session)!;
        if (!string.IsNullOrWhiteSpace(session.TenantId) && principal.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim("tid", session.TenantId));
        }

        return principal;
    }
}
