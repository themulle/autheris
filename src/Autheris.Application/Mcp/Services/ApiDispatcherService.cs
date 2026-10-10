namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.OData.Interfaces;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Universal dynamic API dispatcher and documentation service for MCP agents (ADR-02, ADR-04).
/// </summary>
public sealed class ApiDispatcherService : IApiDispatcherService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IGovernedDataQueryService? _dataQueryService;
    private readonly ICatalogDiscoveryService? _catalogDiscoveryService;
    private readonly IGovernedSqlExecutionService? _sqlExecutionService;
    private readonly IDynamicOpenApiGenerator? _openApiGenerator;
    private readonly ILogger<ApiDispatcherService>? _logger;

    public ApiDispatcherService(
        IGovernedDataQueryService? dataQueryService = null,
        ICatalogDiscoveryService? catalogDiscoveryService = null,
        IGovernedSqlExecutionService? sqlExecutionService = null,
        IDynamicOpenApiGenerator? openApiGenerator = null,
        ILogger<ApiDispatcherService>? logger = null)
    {
        _dataQueryService = dataQueryService;
        _catalogDiscoveryService = catalogDiscoveryService;
        _sqlExecutionService = sqlExecutionService;
        _openApiGenerator = openApiGenerator;
        _logger = logger;
    }

    public Task<string> DescribeApiAsync(string endpoint, string? method = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        var normMethod = (method ?? "GET").Trim().ToUpperInvariant();
        var normEndpoint = endpoint.Trim();

        var desc = BuildEndpointDescription(normEndpoint, normMethod);
        return Task.FromResult(JsonSerializer.Serialize(desc, JsonOpts));
    }

    public async Task<string> InvokeApiAsync(
        string endpoint,
        string method,
        IReadOnlyDictionary<string, object?>? parameters = null,
        object? body = null,
        RequestContext? context = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var effectiveContext = context ?? new RequestContext();
        var normMethod = method.Trim().ToUpperInvariant();
        var normEndpoint = endpoint.Trim().ToLowerInvariant();
        var paramDict = parameters ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // 1. Governed REST Data API: /api/v1/data/{domain}/{table} or /api/v1/data/{datasetId}
        if (normEndpoint.StartsWith("/api/v1/data", StringComparison.OrdinalIgnoreCase))
        {
            if (_dataQueryService == null)
            {
                return SerializeError("ServiceUnavailable", 503, "GovernedDataQueryService is not registered.");
            }

            var table = ExtractTableIdentifier(normEndpoint, paramDict);
            if (table == null)
            {
                return SerializeError("InvalidParams", 400, "Could not resolve dataset/table identifier from endpoint or parameters.");
            }

            var select = ExtractStringList(paramDict, "select");
            var filter = ExtractString(paramDict, "filter");
            var orderBy = ExtractString(paramDict, "orderby") ?? ExtractString(paramDict, "order_by");
            var limit = ExtractInt(paramDict, "limit") ?? 50;
            var offset = ExtractInt(paramDict, "offset") ?? 0;

            var request = new DatasetQueryRequest(table.Value, select, filter, orderBy, limit, offset);
            var envelope = await _dataQueryService.ExecuteQueryAsync(request, effectiveContext, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(envelope, JsonOpts);
        }

        // 2. Catalog datasets list: /api/v1/catalog/datasets
        if (normEndpoint.Equals("/api/v1/catalog/datasets", StringComparison.OrdinalIgnoreCase))
        {
            if (_catalogDiscoveryService == null)
            {
                return SerializeError("ServiceUnavailable", 503, "CatalogDiscoveryService is not registered.");
            }

            var datasets = await _catalogDiscoveryService.ListDatasetsAsync(effectiveContext, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { count = datasets.Count, datasets }, JsonOpts);
        }

        // 3. Catalog dataset detail: /api/v1/catalog/datasets/{datasetId}
        if (normEndpoint.StartsWith("/api/v1/catalog/datasets/", StringComparison.OrdinalIgnoreCase))
        {
            if (_catalogDiscoveryService == null)
            {
                return SerializeError("ServiceUnavailable", 503, "CatalogDiscoveryService is not registered.");
            }

            var datasetId = normEndpoint["/api/v1/catalog/datasets/".Length..].Trim('/');
            if (McpDatasetCatalog.TryParseDatasetId(datasetId, out var tableId))
            {
                var detail = await _catalogDiscoveryService.GetDatasetDetailAsync(tableId, effectiveContext, ct).ConfigureAwait(false);
                return detail != null
                    ? JsonSerializer.Serialize(detail, JsonOpts)
                    : SerializeError("NotFound", 404, $"Dataset '{datasetId}' not found or access denied.");
            }

            return SerializeError("InvalidParams", 400, $"Invalid dataset id '{datasetId}'. Format: domain.schema.table.");
        }

        // 4. Catalog search: /api/v1/catalog/search
        if (normEndpoint.StartsWith("/api/v1/catalog/search", StringComparison.OrdinalIgnoreCase))
        {
            if (_catalogDiscoveryService == null)
            {
                return SerializeError("ServiceUnavailable", 503, "CatalogDiscoveryService is not registered.");
            }

            var q = ExtractString(paramDict, "q") ?? ExtractString(paramDict, "query") ?? "";
            var domain = ExtractString(paramDict, "domain");
            var results = await _catalogDiscoveryService.SearchCatalogAsync(q, domain, effectiveContext, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { query = q, domain, count = results.Count, results }, JsonOpts);
        }

        // 5. Catalog datasources: /api/v1/catalog/datasources
        if (normEndpoint.StartsWith("/api/v1/catalog/datasources", StringComparison.OrdinalIgnoreCase))
        {
            if (_catalogDiscoveryService == null)
            {
                return SerializeError("ServiceUnavailable", 503, "CatalogDiscoveryService is not registered.");
            }

            var datasources = await _catalogDiscoveryService.ListDatasourcesAsync(effectiveContext, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { count = datasources.Count, datasources }, JsonOpts);
        }

        // 6. WebSQL: /api/v1/statement
        if (normEndpoint.Equals("/api/v1/statement", StringComparison.OrdinalIgnoreCase) && normMethod == "POST")
        {
            if (_sqlExecutionService == null)
            {
                return SerializeError("ServiceUnavailable", 503, "GovernedSqlExecutionService is not registered.");
            }

            var sql = ExtractString(paramDict, "sql") ?? ExtractString(paramDict, "query");
            if (string.IsNullOrWhiteSpace(sql) && body != null)
            {
                try
                {
                    if (body is JsonElement jsonElem)
                    {
                        if (jsonElem.TryGetProperty("sql", out var s) || jsonElem.TryGetProperty("query", out s))
                        {
                            sql = s.GetString();
                        }
                    }
                    else if (body is string rawJson && !string.IsNullOrWhiteSpace(rawJson))
                    {
                        using var doc = JsonDocument.Parse(rawJson);
                        if (doc.RootElement.TryGetProperty("sql", out var s) || doc.RootElement.TryGetProperty("query", out s))
                        {
                            sql = s.GetString();
                        }
                    }
                    else
                    {
                        var serialized = JsonSerializer.Serialize(body);
                        using var doc = JsonDocument.Parse(serialized);
                        if (doc.RootElement.TryGetProperty("sql", out var s) || doc.RootElement.TryGetProperty("query", out s))
                        {
                            sql = s.GetString();
                        }
                    }
                }
                catch (JsonException) { }
            }

            if (string.IsNullOrWhiteSpace(sql))
            {
                return SerializeError("InvalidParams", 400, "Missing required 'sql' or 'query' parameter for WebSQL execution.");
            }

            var sqlResult = await _sqlExecutionService.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest(sql),
                effectiveContext.User,
                effectiveContext.TenantId,
                ct).ConfigureAwait(false);

            return JsonSerializer.Serialize(sqlResult, JsonOpts);
        }

        // 7. Governance access: /api/v1/governance/me/access
        if (normEndpoint.Equals("/api/v1/governance/me/access", StringComparison.OrdinalIgnoreCase))
        {
            var accessInfo = new
            {
                subjectId = effectiveContext.SubjectId.Value,
                tenantId = effectiveContext.TenantId.Value,
                roles = effectiveContext.Roles ?? [],
                correlationId = effectiveContext.CorrelationId,
                clientIp = effectiveContext.ClientIp
            };
            return JsonSerializer.Serialize(accessInfo, JsonOpts);
        }

        return SerializeError("EndpointNotFound", 404, $"Endpoint '{endpoint}' [{method}] is not recognized by Autheris dynamic dispatcher.");
    }

    public async Task<string> GetOpenApiJsonAsync(CancellationToken ct = default)
    {
        if (_openApiGenerator != null)
        {
            try
            {
                return await _openApiGenerator.GenerateOpenApiJsonAsync(ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Dynamic OpenAPI generator failed; falling back to static specification.");
            }
        }

        return BuildStaticOpenApiJson();
    }

    public Task<string> GetEndpointsDocumentationAsync(CancellationToken ct = default)
    {
        const string doc = """
            # Autheris Enterprise Gateway - REST API Documentation

            ## 1. Governed REST Data API
            - **GET /api/v1/data/{domain}/{schema}/{table}**
              - Description: Queries dataset records under tenant isolation, row filters and column masking.
              - Query parameters:
                - `select` (string): Comma-separated columns (e.g. `id,amount,status`).
                - `filter` (string): Safe AST predicate (e.g. `status eq 'active' and amount gt 100`).
                - `orderBy` (string): Sorting expression (e.g. `createdDate desc`).
                - `limit` (int): Page size (default 50, max 1000).
                - `offset` (int): Offset paging.
              - Returns: `DatasetQueryEnvelope` with metadata and rows.

            ## 2. Catalog & Discovery API
            - **GET /api/v1/catalog/datasets**: Lists all datasets visible to the caller (ReBAC filtered).
            - **GET /api/v1/catalog/datasets/{datasetId}**: Returns detailed column schema and masking states.
            - **GET /api/v1/catalog/datasources**: Lists registered datasources without leaking secrets.
            - **GET /api/v1/catalog/search?q={query}&domain={domain}**: Fulltext search over datasets and columns.

            ## 3. WebSQL & Statement Execution
            - **POST /api/v1/statement**
              - Description: Executes governed SQL statements with automatic AST rewriting.
              - Request body: `{ "sql": "SELECT id, email FROM sales.public.customers" }`.

            ## 4. Governance & Access Self-Service
            - **GET /api/v1/governance/me/access**: Transparent view of current identity, roles, and access boundaries.
            """;
        return Task.FromResult(doc);
    }

    public Task<string> GetMcpToolsDocumentationAsync(CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Autheris Model Context Protocol (MCP) Tools Reference\n");
        foreach (var tool in McpDatasetTools.Definitions())
        {
            sb.AppendLine($"### `{tool.Name}`");
            sb.AppendLine($"{tool.Description}\n");
            sb.AppendLine("**Input Schema:**");
            sb.AppendLine("```json");
            sb.AppendLine(tool.InputJsonSchema);
            sb.AppendLine("```\n");
        }
        return Task.FromResult(sb.ToString());
    }

    private static object BuildEndpointDescription(string endpoint, string method)
    {
        if (endpoint.StartsWith("/api/v1/data", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                endpoint = "/api/v1/data/{domain}/{table}",
                method = "GET",
                summary = "Governed REST Data Query API",
                description = "Queries dataset records with server-side column masking and mandatory row filtering.",
                parameters = new object[]
                {
                    new { name = "domain", @in = "path", required = true, type = "string", description = "Business domain" },
                    new { name = "table", @in = "path", required = true, type = "string", description = "Table name" },
                    new { name = "select", @in = "query", required = false, type = "string", description = "Comma-separated columns" },
                    new { name = "filter", @in = "query", required = false, type = "string", description = "Predicate expression" },
                    new { name = "orderBy", @in = "query", required = false, type = "string", description = "Sort expression" },
                    new { name = "limit", @in = "query", required = false, type = "integer", @default = 50 },
                    new { name = "offset", @in = "query", required = false, type = "integer", @default = 0 }
                },
                responses = new
                {
                    _200 = new { description = "Dataset query envelope with columns and rows", type = "DatasetQueryEnvelope" },
                    _400 = new { description = "Invalid query or predicate targeting denied columns" },
                    _403 = new { description = "Access denied by ReBAC or table policy" }
                }
            };
        }

        return new
        {
            endpoint,
            method,
            summary = $"Autheris Gateway Endpoint: {endpoint}",
            parameters = Array.Empty<object>(),
            responses = new { _200 = new { description = "Successful operation" } }
        };
    }

    private static TableIdentifier? ExtractTableIdentifier(string endpoint, IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("dataset", out var dsObj) && dsObj is string dsStr && McpDatasetCatalog.TryParseDatasetId(dsStr, out var tid))
        {
            return tid;
        }

        var segments = endpoint.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // segments: ["api", "v1", "data", {domain}, {table}]
        if (segments.Length >= 5 && segments[0] == "api" && segments[1] == "v1" && segments[2] == "data")
        {
            var domain = segments[3];
            var schema = segments.Length >= 6 ? segments[4] : "public";
            var table = segments.Length >= 6 ? segments[5] : segments[4];
            return new TableIdentifier(domain, schema, table);
        }

        return null;
    }

    private static string? ExtractString(IReadOnlyDictionary<string, object?> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && val != null)
        {
            return val is JsonElement je ? je.ToString() : val.ToString();
        }
        return null;
    }

    private static int? ExtractInt(IReadOnlyDictionary<string, object?> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && val != null)
        {
            if (val is int i) return i;
            if (val is JsonElement je && je.TryGetInt32(out var parsed)) return parsed;
            if (int.TryParse(val.ToString(), out var parsedStr)) return parsedStr;
        }
        return null;
    }

    private static IReadOnlyList<string>? ExtractStringList(IReadOnlyDictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val) || val == null) return null;
        if (val is IEnumerable<string> list) return list.ToList();
        if (val is string s) return s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            return je.EnumerateArray().Select(e => e.GetString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList();
        }
        return null;
    }

    private static string SerializeError(string error, int status, string message) =>
        JsonSerializer.Serialize(new { error, status, message }, JsonOpts);

    private static string BuildStaticOpenApiJson() => """
        {
          "openapi": "3.1.0",
          "info": {
            "title": "Autheris Enterprise Governance Gateway API",
            "version": "2.0.0",
            "description": "Governed REST Data API, Catalog Discovery, and WebSQL execution."
          },
          "paths": {
            "/api/v1/data/{domain}/{table}": {
              "get": {
                "summary": "Governed REST Data API",
                "responses": { "200": { "description": "Dataset query envelope" } }
              }
            },
            "/api/v1/catalog/datasets": {
              "get": {
                "summary": "List catalog datasets",
                "responses": { "200": { "description": "Datasets list" } }
              }
            },
            "/api/v1/catalog/datasources": {
              "get": {
                "summary": "List datasources",
                "responses": { "200": { "description": "Datasources list" } }
              }
            },
            "/api/v1/statement": {
              "post": {
                "summary": "Execute WebSQL statement",
                "responses": { "200": { "description": "Governed SQL query result" } }
              }
            }
          }
        }
        """;
}
