namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Domain.Model;

public static class McpDocTools
{
    public const string SearchDocs = "search_docs";
    public const string LookupErrorCode = "lookup_error_code";
    public const string GetProcedure = "get_procedure";
    public const string ListDocTopics = "list_doc_topics";
    public const string ListApplications = "list_applications";
    public const string DepositDocumentation = "deposit_documentation";
    public static bool IsDocTool(string? toolName) =>
        string.Equals(toolName, SearchDocs, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, LookupErrorCode, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, GetProcedure, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, ListDocTopics, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, ListApplications, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, DepositDocumentation, StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<string> ExecuteDocToolAsync(
        string toolName,
        string argumentsJson,
        IDocSearchService? searchService,
        IUniversalDocStore? docStore,
        McpSessionContext? sessionContext = null,
        CancellationToken ct = default)
    {
        if (string.Equals(toolName, SearchDocs, StringComparison.OrdinalIgnoreCase))
        {
            if (searchService == null)
            {
                return JsonSerializer.Serialize(new { error = "Doc search service is not configured." });
            }
            return await ExecuteSearchDocsAsync(searchService, argumentsJson, ct).ConfigureAwait(false);
        }

        if (string.Equals(toolName, LookupErrorCode, StringComparison.OrdinalIgnoreCase))
        {
            if (searchService == null)
            {
                return JsonSerializer.Serialize(new { error = "Doc search service is not configured." });
            }
            return await ExecuteLookupErrorCodeAsync(searchService, argumentsJson, ct).ConfigureAwait(false);
        }

        if (string.Equals(toolName, GetProcedure, StringComparison.OrdinalIgnoreCase))
        {
            if (searchService == null)
            {
                return JsonSerializer.Serialize(new { error = "Doc search service is not configured." });
            }
            return await ExecuteGetProcedureAsync(searchService, argumentsJson, ct).ConfigureAwait(false);
        }

        if (string.Equals(toolName, ListDocTopics, StringComparison.OrdinalIgnoreCase))
        {
            if (searchService == null)
            {
                return JsonSerializer.Serialize(new { error = "Doc search service is not configured." });
            }
            return await ExecuteListDocTopicsAsync(searchService, argumentsJson, ct).ConfigureAwait(false);
        }

        if (string.Equals(toolName, ListApplications, StringComparison.OrdinalIgnoreCase))
        {
            if (docStore == null)
            {
                return JsonSerializer.Serialize(new { error = "Universal doc store is not configured." });
            }
            return await ExecuteListApplicationsAsync(docStore, argumentsJson, ct).ConfigureAwait(false);
        }

        if (string.Equals(toolName, DepositDocumentation, StringComparison.OrdinalIgnoreCase))
        {
            if (docStore == null)
            {
                return JsonSerializer.Serialize(new { error = "Universal doc store is not configured." });
            }
            return await ExecuteDepositDocumentationAsync(docStore, argumentsJson, sessionContext, ct).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize(new { error = $"Unknown doc tool '{toolName}'." });
    }

    public static IEnumerable<McpToolDefinition> Definitions()
    {
        yield return new McpToolDefinition(
            Name: SearchDocs,
            Description: "Searches local offline documentation, architecture docs (arc42, ADRs), API specifications, and operational runbooks using keyword and BM25 hybrid ranking.",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["query"],
              "properties": {
                "query": { "type": "string", "description": "Search terms (keywords, topics, error codes or concepts)." },
                "category": { "type": "string", "enum": ["Runbook", "Architecture", "Adr", "ApiEndpoint", "ErrorCode", "Configuration", "Guide"], "description": "Optional category filter." },
                "limit": { "type": "integer", "description": "Maximum number of matching sections to return (default 5)." }
              }
            }
            """,
            TargetGraphQLOperation: SearchDocs);

        yield return new McpToolDefinition(
            Name: LookupErrorCode,
            Description: "Looks up an error code (e.g. 'ERR_CASBIN_DENIED', '403 Forbidden') to retrieve symptom, root cause, resolution steps and referenced runbooks.",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["errorCode"],
              "properties": {
                "errorCode": { "type": "string", "description": "Error code identifier." }
              }
            }
            """,
            TargetGraphQLOperation: LookupErrorCode);

        yield return new McpToolDefinition(
            Name: GetProcedure,
            Description: "Retrieves the exact markdown procedure or runbook instructions for a given document chunk identifier.",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["identifier"],
              "properties": {
                "identifier": { "type": "string", "description": "Chunk identifier (e.g. 'autheris::Runbook::operations-runbook#break-glass')." }
              }
            }
            """,
            TargetGraphQLOperation: GetProcedure);

        yield return new McpToolDefinition(
            Name: ListDocTopics,
            Description: "Lists all available documentation topics and runbooks indexed in the offline documentation gateway.",
            InputJsonSchema: """
            {
              "type": "object",
              "properties": {
                "appId": { "type": "string", "description": "Optional application filter (e.g. 'autheris')." }
              }
            }
            """,
            TargetGraphQLOperation: ListDocTopics);

        yield return new McpToolDefinition(
            Name: ListApplications,
            Description: "Lists all registered applications, business domains, versions, and governance tiers in the universal document lake.",
            InputJsonSchema: """
            {
              "type": "object",
              "properties": {
                "domain": { "type": "string", "description": "Optional business domain filter (e.g. 'finance', 'core')." },
                "tag": { "type": "string", "description": "Optional tag filter (e.g. 'PCI-DSS', 'Tier-1')." }
              }
            }
            """,
            TargetGraphQLOperation: ListApplications);

        yield return new McpToolDefinition(
            Name: DepositDocumentation,
            Description: "Deposits or updates documentation, a troubleshooting runbook, or an error resolution guide directly into the enterprise universal document store.",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["appId", "category", "title", "content"],
              "properties": {
                "appId": { "type": "string", "description": "Application identifier (e.g. 'billing-service', 'autheris')." },
                "category": { "type": "string", "enum": ["Runbook", "Architecture", "Adr", "ApiEndpoint", "ErrorCode", "Configuration", "Guide"], "description": "Document category." },
                "title": { "type": "string", "description": "Title of the document or runbook." },
                "content": { "type": "string", "description": "Markdown content or specification text." },
                "docId": { "type": "string", "description": "Optional custom document ID/slug." },
                "errorCode": { "type": "string", "description": "Optional associated error code." },
                "tags": { "type": "array", "items": { "type": "string" }, "description": "Optional search tags." }
              }
            }
            """,
            TargetGraphQLOperation: DepositDocumentation);
    }

    public static async Task<string> ExecuteSearchDocsAsync(
        IDocSearchService searchService,
        string? argumentsJson,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(searchService);

        string? query = null;
        DocCategory? category = null;
        int limit = 5;

        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String)
                {
                    query = q.GetString();
                }
                if (root.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String &&
                    Enum.TryParse<DocCategory>(c.GetString(), true, out var parsedCat))
                {
                    category = parsedCat;
                }
                if (root.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number)
                {
                    limit = l.GetInt32();
                }
            }
            catch (JsonException)
            {
            }
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return JsonSerializer.Serialize(new { error = "Argument 'query' is required." });
        }

        var results = await searchService.SearchAsync(query, category, limit, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            total = results.Count,
            results = results
        }, IndentedOptions);
    }

    public static async Task<string> ExecuteLookupErrorCodeAsync(
        IDocSearchService searchService,
        string? argumentsJson,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(searchService);

        string? errorCode = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("errorCode", out var ec) && ec.ValueKind == JsonValueKind.String)
                {
                    errorCode = ec.GetString();
                }
            }
            catch (JsonException)
            {
            }
        }

        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return JsonSerializer.Serialize(new { error = "Argument 'errorCode' is required." });
        }

        var def = await searchService.LookupErrorCodeAsync(errorCode, ct).ConfigureAwait(false);
        if (def == null)
        {
            return JsonSerializer.Serialize(new { error = $"Error code '{errorCode}' was not found in documentation." });
        }

        return JsonSerializer.Serialize(def, IndentedOptions);
    }

    public static async Task<string> ExecuteGetProcedureAsync(
        IDocSearchService searchService,
        string? argumentsJson,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(searchService);

        string? identifierStr = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("identifier", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                {
                    identifierStr = idProp.GetString();
                }
            }
            catch (JsonException)
            {
            }
        }

        if (string.IsNullOrWhiteSpace(identifierStr) || !DocChunkIdentifier.TryParse(identifierStr, out var identifier))
        {
            return JsonSerializer.Serialize(new { error = "A valid 'identifier' (AppId::Category::DocId#SectionId) is required." });
        }

        var chunk = await searchService.GetChunkAsync(identifier!, ct).ConfigureAwait(false);
        if (chunk == null)
        {
            return JsonSerializer.Serialize(new { error = $"Document section '{identifierStr}' was not found." });
        }

        return JsonSerializer.Serialize(chunk, IndentedOptions);
    }

    public static async Task<string> ExecuteListDocTopicsAsync(
        IDocSearchService searchService,
        string? argumentsJson,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(searchService);

        string? appId = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("appId", out var aProp) && aProp.ValueKind == JsonValueKind.String)
                {
                    appId = aProp.GetString();
                }
            }
            catch (JsonException)
            {
            }
        }

        var topics = await searchService.ListTopicsAsync(appId, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            total = topics.Count,
            topics = topics
        }, IndentedOptions);
    }

    public static async Task<string> ExecuteListApplicationsAsync(
        IUniversalDocStore docStore,
        string? argumentsJson,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(docStore);

        string? domain = null;
        string? tag = null;

        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("domain", out var dProp) && dProp.ValueKind == JsonValueKind.String)
                {
                    domain = dProp.GetString();
                }
                if (root.TryGetProperty("tag", out var tProp) && tProp.ValueKind == JsonValueKind.String)
                {
                    tag = tProp.GetString();
                }
            }
            catch (JsonException)
            {
            }
        }

        var apps = await docStore.ListApplicationsAsync(domain, tag, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            total = apps.Count,
            applications = apps
        }, IndentedOptions);
    }

    public static async Task<string> ExecuteDepositDocumentationAsync(
        IUniversalDocStore docStore,
        string? argumentsJson,
        McpSessionContext? sessionContext = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(docStore);

        if (sessionContext != null)
        {
            if (sessionContext.IsReadOnly)
            {
                return JsonSerializer.Serialize(new { error = "ReadOnlySession", message = "Cannot deposit documentation in a read-only MCP session." });
            }

            var roles = sessionContext.Roles ?? [];
            bool isAdmin = roles.Contains("ClusterAdmin", StringComparer.OrdinalIgnoreCase) ||
                           roles.Contains("GovernanceAdmin", StringComparer.OrdinalIgnoreCase);
            if (!isAdmin)
            {
                return JsonSerializer.Serialize(new { error = "Forbidden", message = "Depositing documentation requires ClusterAdmin or GovernanceAdmin role." });
            }
        }

        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return JsonSerializer.Serialize(new { error = "Arguments payload cannot be empty." });
        }

        string appId;
        DocCategory category = DocCategory.Runbook;
        string title;
        string content;
        string? docId = null;
        string? errorCode = null;
        List<string>? tags = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("appId", out var aProp) || aProp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(aProp.GetString()))
            {
                return JsonSerializer.Serialize(new { error = "Property 'appId' is required." });
            }
            appId = aProp.GetString()!;

            if (sessionContext != null)
            {
                var roles = sessionContext.Roles ?? [];
                var isGlobalApp = string.Equals(appId, "autheris", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(appId, "global", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(appId, "system", StringComparison.OrdinalIgnoreCase);

                if (isGlobalApp && !roles.Contains("ClusterAdmin", StringComparer.OrdinalIgnoreCase))
                {
                    return JsonSerializer.Serialize(new { error = "Forbidden", message = "Depositing documentation for global application requires ClusterAdmin role." });
                }
            }

            if (root.TryGetProperty("category", out var cProp) && cProp.ValueKind == JsonValueKind.String)
            {
                if (Enum.TryParse<DocCategory>(cProp.GetString(), true, out var parsedCat))
                {
                    category = parsedCat;
                }
            }

            if (!root.TryGetProperty("title", out var tProp) || tProp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tProp.GetString()))
            {
                return JsonSerializer.Serialize(new { error = "Property 'title' is required." });
            }
            title = tProp.GetString()!;

            if (!root.TryGetProperty("content", out var ctProp) || ctProp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(ctProp.GetString()))
            {
                return JsonSerializer.Serialize(new { error = "Property 'content' is required." });
            }
            content = ctProp.GetString()!;

            if (root.TryGetProperty("docId", out var dProp) && dProp.ValueKind == JsonValueKind.String)
            {
                docId = dProp.GetString();
            }
            if (root.TryGetProperty("errorCode", out var eProp) && eProp.ValueKind == JsonValueKind.String)
            {
                errorCode = eProp.GetString();
            }
            if (root.TryGetProperty("tags", out var tgProp) && tgProp.ValueKind == JsonValueKind.Array)
            {
                tags = new List<string>();
                foreach (var item in tgProp.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                    {
                        tags.Add(s);
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new { error = $"Invalid JSON arguments: {ex.Message}" });
        }

        var request = new DepositDocRequest(appId, category, title, content, docId, errorCode, tags);
        var result = await docStore.DepositDocumentAsync(request, ct).ConfigureAwait(false);

        var scrubbedResult = result with { FilePath = null };
        return JsonSerializer.Serialize(scrubbedResult, IndentedOptions);
    }
}
