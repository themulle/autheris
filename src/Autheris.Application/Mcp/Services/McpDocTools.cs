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

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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
}
