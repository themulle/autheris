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
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

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

    public McpToolExecutionHandler(
        IGovernedSqlExecutionService sqlExecutionService,
        IGovernedDataQueryService dataQueryService,
        ICatalogDiscoveryService catalogDiscoveryService,
        IApiDispatcherService apiDispatcherService,
        ILineageGraphStore lineageGraphStore,
        ITableMetadataRepository metadataRepo)
    {
        _sqlExecutionService = sqlExecutionService ?? throw new ArgumentNullException(nameof(sqlExecutionService));
        _dataQueryService = dataQueryService ?? throw new ArgumentNullException(nameof(dataQueryService));
        _catalogDiscoveryService = catalogDiscoveryService ?? throw new ArgumentNullException(nameof(catalogDiscoveryService));
        _apiDispatcherService = apiDispatcherService ?? throw new ArgumentNullException(nameof(apiDispatcherService));
        _lineageGraphStore = lineageGraphStore ?? throw new ArgumentNullException(nameof(lineageGraphStore));
        _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
    }

    public bool CanHandle(string toolName) =>
        string.Equals(toolName, McpDatasetTools.QuerySql, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.QueryDataset, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.SearchCatalog, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.GetMyPermissions, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.ListDatasources, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.GetDataLineage, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.DescribeApi, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, McpDatasetTools.InvokeApi, StringComparison.OrdinalIgnoreCase);

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
                new GovernedSqlQueryRequest(query),
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

            var datasetRequest = new DatasetQueryRequest(tableId, select, filter, orderBy, limit, offset);
            var result = await _dataQueryService.ExecuteQueryAsync(datasetRequest, requestContext, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(result, JsonOpts);
        }

        if (string.Equals(tool.Name, McpDatasetTools.SearchCatalog, StringComparison.OrdinalIgnoreCase))
        {
            var query = root.TryGetProperty("query", out var qProp) ? qProp.GetString() ?? "" : "";
            var domain = root.TryGetProperty("domain", out var dProp) ? dProp.GetString() : null;

            var results = await _catalogDiscoveryService.SearchCatalogAsync(query, domain, requestContext, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(results, JsonOpts);
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
            if (node == null)
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

        throw new NotSupportedException($"Tool '{tool.Name}' is not supported by McpToolExecutionHandler.");
    }

    private static ClaimsPrincipal BuildUserPrincipal(McpSessionContext session)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, session.ServicePrincipalId),
            new("sub", session.UserSid ?? session.ServicePrincipalId),
            new("tid", session.TenantId)
        };

        if (session.Roles != null)
        {
            foreach (var role in session.Roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "McpSessionAuth"));
    }
}
