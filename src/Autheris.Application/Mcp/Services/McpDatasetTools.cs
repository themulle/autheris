namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Text.Json;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// The built-in MCP dataset tools: names, tool definitions and the table a call targets.
/// </summary>
public static class McpDatasetTools
{
    public const string ListDatasets = "list_datasets";
    public const string DescribeDataset = "describe_dataset";
    public const string SampleRows = "sample_rows";
    public const string QueryGraphQl = "query_graphql";

    /// <summary>Sent as MCP <c>instructions</c> on initialize: how an agent finds and queries data.</summary>
    public const string ServerInstructions =
        "Autheris is a governed data gateway. To answer questions with data: " +
        "1) call list_datasets to find the datasets you may use, " +
        "2) call describe_dataset for columns, types, the GraphQL field and an example query, " +
        "3) query with query_graphql - GraphQL is the preferred query path (filter, sort, paging and relations in one query; always pass a small first). " +
        "sample_rows shows a few rows. Other protocols (OData, OpenAPI/REST, WebSQL, procedures, OLAP, Arrow) are listed by list_datasets and describe_dataset. " +
        "Results are filtered and masked for your identity; hidden datasets and columns do not appear.";

    /// <summary>ABAC object for catalog listings (like <c>query_data_catalog</c>).</summary>
    public static readonly TableIdentifier CatalogTable = new("governance", "catalog", "datasets");

    public static bool IsDatasetTool(string? toolName) =>
        string.Equals(toolName, ListDatasets, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, DescribeDataset, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, SampleRows, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, QueryGraphQl, StringComparison.OrdinalIgnoreCase);

    /// <summary>Tools that return rows; only these need four-eyes approval on a four-eyes table.</summary>
    public static bool ReadsRows(string? toolName) =>
        string.Equals(toolName, SampleRows, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, QueryGraphQl, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The table a dataset tool call targets. Null (fail-closed) when a table tool has no valid <c>dataset</c> argument.
    /// </summary>
    public static TableIdentifier? TargetTable(string toolName, string? argumentsJson)
    {
        if (string.Equals(toolName, ListDatasets, StringComparison.OrdinalIgnoreCase))
        {
            return CatalogTable;
        }

        if (string.Equals(toolName, QueryGraphQl, StringComparison.OrdinalIgnoreCase))
        {
            // The tables of a GraphQL document come from the catalog schema (IGraphQlCatalogMap), not from one argument.
            return null;
        }

        return TryGetStringArgument(argumentsJson, "dataset", out var dataset) &&
               McpDatasetCatalog.TryParseDatasetId(dataset, out var id)
            ? id
            : null;
    }

    public static IEnumerable<McpToolDefinition> Definitions()
    {
        yield return new McpToolDefinition(
            Name: QueryGraphQl,
            Description: "Preferred way to query data. Runs a read-only GraphQL query against the gateway: filter (where), sort (orderBy), page (first, offset) and follow relations between datasets. Get the field names and an example query from describe_dataset.",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["query"],
              "properties": {
                "query": { "type": "string", "description": "One GraphQL query operation, e.g. query { sales_public_orders(where: { status: { eq: \"open\" } }, first: 20) { id amount } }." },
                "variables": { "type": "object", "description": "Optional values for the variables of the query." }
              }
            }
            """,
            TargetGraphQLOperation: QueryGraphQl);

        yield return new McpToolDefinition(
            Name: ListDatasets,
            Description: "Lists the datasets (tables) you may query with their GraphQL field, and all protocols of the gateway. Start here, then call describe_dataset and query with query_graphql.",
            InputJsonSchema: """
            {
              "type": "object",
              "properties": {
                "search": { "type": "string", "description": "Optional text matched against dataset ids, descriptions and column names." },
                "domain": { "type": "string", "description": "Optional business domain filter (e.g. 'sales')." }
              }
            }
            """,
            TargetGraphQLOperation: ListDatasets);

        yield return new McpToolDefinition(
            Name: DescribeDataset,
            Description: "Describes a dataset: the columns you may see with database and GraphQL types, the primary key, sensitivity, how to query it with GraphQL (field, filter and sort types, example query, relations), the other access paths and curated example queries.",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["dataset"],
              "properties": {
                "dataset": { "type": "string", "description": "Dataset id as returned by list_datasets (domain.schema.table)." }
              }
            }
            """,
            TargetGraphQLOperation: DescribeDataset);

        yield return new McpToolDefinition(
            Name: SampleRows,
            Description: "Returns a few rows of a dataset, with your row filters and column masking applied.",
            InputJsonSchema: $$"""
            {
              "type": "object",
              "required": ["dataset"],
              "properties": {
                "dataset": { "type": "string", "description": "Dataset id as returned by list_datasets (domain.schema.table)." },
                "count": { "type": "integer", "minimum": 1, "maximum": {{McpDatasetCatalog.MaxSampleRows}}, "description": "Number of rows (default {{McpDatasetCatalog.DefaultSampleRows}})." }
              }
            }
            """,
            TargetGraphQLOperation: SampleRows);
    }

    /// <summary>
    /// A string argument that occurs exactly once (case-insensitive). The executor reads arguments case-insensitively
    /// with the last value winning, so a repeated name could make the guardrail check something else than is executed.
    /// </summary>
    public static bool TryGetStringArgument(string? argumentsJson, string name, out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // The executor reads arguments case-insensitively (last one wins), so a name that occurs more than once
            // could make ABAC check a different table than the one that is read: such calls are rejected.
            var found = 0;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    found++;
                    value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                }
            }

            return found == 1 && value != null;
        }
        catch (JsonException)
        {
        }

        return false;
    }
}
