namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Text.Json;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// The built-in MCP dataset and hybrid tools: names, tool definitions and target tables.
/// </summary>
public static class McpDatasetTools
{
    public const string ListDatasets = "list_datasets";
    public const string DescribeDataset = "describe_dataset";
    public const string SampleRows = "sample_rows";
    public const string QueryGraphQl = "query_graphql";

    // Track D: Hybrid MCP Tools (ADR-02)
    public const string QuerySql = "query_sql";
    public const string QueryDataset = "query_dataset";
    public const string SearchCatalog = "search_catalog";
    public const string GetMyPermissions = "get_my_permissions";
    public const string ListDatasources = "list_datasources";
    public const string GetDataLineage = "get_data_lineage";
    public const string DescribeApi = "describe_api";
    public const string InvokeApi = "invoke_api";

    /// <summary>Sent as MCP <c>instructions</c> on initialize: how an agent finds and queries data.</summary>
    public const string ServerInstructions =
        "Autheris is a governed data gateway. To answer questions with data: " +
        "1) call list_datasets or search_catalog to find datasets you may use, " +
        "2) call describe_dataset or get_my_permissions for columns, types and access permissions, " +
        "3) query with query_sql, query_dataset or query_graphql. " +
        "sample_rows shows a few sample rows. Use describe_api and invoke_api for dynamic REST access. " +
        "Results are filtered and masked for your identity; hidden datasets and columns do not appear.";

    /// <summary>ABAC object for catalog listings (like <c>query_data_catalog</c>).</summary>
    public static readonly TableIdentifier CatalogTable = new("governance", "catalog", "datasets");

    public static bool IsDatasetTool(string? toolName) =>
        string.Equals(toolName, ListDatasets, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, DescribeDataset, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, SampleRows, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, QueryGraphQl, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, QuerySql, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, QueryDataset, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, SearchCatalog, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, GetMyPermissions, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, ListDatasources, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, GetDataLineage, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, DescribeApi, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, InvokeApi, StringComparison.OrdinalIgnoreCase);

    /// <summary>Tools that return rows; only these need four-eyes approval on a four-eyes table.</summary>
    public static bool ReadsRows(string? toolName) =>
        string.Equals(toolName, SampleRows, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, QueryGraphQl, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, QuerySql, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, QueryDataset, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The table a dataset tool call targets. Null (fail-closed) when a table tool has no valid <c>dataset</c> argument.
    /// </summary>
    public static TableIdentifier? TargetTable(string toolName, string? argumentsJson)
    {
        if (string.Equals(toolName, ListDatasets, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(toolName, SearchCatalog, StringComparison.OrdinalIgnoreCase))
        {
            return CatalogTable;
        }

        if (string.Equals(toolName, ListDatasources, StringComparison.OrdinalIgnoreCase))
        {
            return new TableIdentifier("governance", "catalog", "datasources");
        }

        if (string.Equals(toolName, DescribeApi, StringComparison.OrdinalIgnoreCase))
        {
            return new TableIdentifier("governance", "api", "describe");
        }

        if (string.Equals(toolName, InvokeApi, StringComparison.OrdinalIgnoreCase))
        {
            return new TableIdentifier("governance", "api", "invoke");
        }

        if (string.Equals(toolName, QuerySql, StringComparison.OrdinalIgnoreCase))
        {
            return new TableIdentifier("governance", "sql", "query");
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
                "domain": { "type": "string", "description": "Optional business domain filter (e.g. 'sales')." },
                "offset": { "type": "integer", "minimum": 0, "description": "Optional pagination offset (default 0)." },
                "limit": { "type": "integer", "minimum": 1, "maximum": 100, "description": "Optional maximum datasets to return per page (default 50, max 100)." }
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

        yield return new McpToolDefinition(
            Name: QuerySql,
            Description: "Executes a governed SQL query with row-level security, column masking, and tenant isolation under the caller's context.",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["query"],
              "properties": {
                "query": { "type": "string", "description": "SQL SELECT query to execute against the gateway." }
              }
            }
            """,
            TargetGraphQLOperation: QuerySql);

        yield return new McpToolDefinition(
            Name: QueryDataset,
            Description: "Queries a dataset (table) by identifier with optional column selection, filter expression, ordering, limit and offset.",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["dataset"],
              "properties": {
                "dataset": { "type": "string", "description": "Dataset identifier (domain.schema.table)." },
                "select": { "type": "array", "items": { "type": "string" }, "description": "Optional list of columns to select." },
                "filter": { "type": "string", "description": "Optional filter predicate expression." },
                "orderBy": { "type": "string", "description": "Optional sort order (e.g. 'createdDate desc')." },
                "limit": { "type": "integer", "minimum": 1, "maximum": 1000, "description": "Maximum rows to return (default 50)." },
                "offset": { "type": "integer", "minimum": 0, "description": "Number of rows to skip (default 0)." }
              }
            }
            """,
            TargetGraphQLOperation: QueryDataset);

        yield return new McpToolDefinition(
            Name: SearchCatalog,
            Description: "Searches the data catalog for datasets by keyword, domain or column names.",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["query"],
              "properties": {
                "query": { "type": "string", "description": "Search query text." },
                "domain": { "type": "string", "description": "Optional business domain filter." }
              }
            }
            """,
            TargetGraphQLOperation: SearchCatalog);

        yield return new McpToolDefinition(
            Name: GetMyPermissions,
            Description: "Checks effective permissions, ReBAC authorization, column masking rules and row filters for the current caller on a dataset.",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["dataset"],
              "properties": {
                "dataset": { "type": "string", "description": "Dataset identifier (domain.schema.table)." }
              }
            }
            """,
            TargetGraphQLOperation: GetMyPermissions);

        yield return new McpToolDefinition(
            Name: ListDatasources,
            Description: "Lists configured enterprise data sources and connectors with their health status and domain (without leaking secrets).",
            InputJsonSchema: """
            {
              "type": "object",
              "properties": {
                "status": { "type": "string", "description": "Optional status filter (e.g. healthy, active, degraded, inactive)." }
              }
            }
            """,
            TargetGraphQLOperation: ListDatasources);

        yield return new McpToolDefinition(
            Name: GetDataLineage,
            Description: "Retrieves provenance and data lineage graph for a dataset, including upstream sources and downstream consumers.",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["dataset"],
              "properties": {
                "dataset": { "type": "string", "description": "Dataset identifier (domain.schema.table)." }
              }
            }
            """,
            TargetGraphQLOperation: GetDataLineage);

        yield return new McpToolDefinition(
            Name: DescribeApi,
            Description: "Inspects API endpoint schemas, parameters, required permissions, and request/response contracts.",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["endpoint"],
              "properties": {
                "endpoint": { "type": "string", "description": "API route path (e.g. '/api/v1/data/{domain}/{table}')." },
                "method": { "type": "string", "description": "Optional HTTP method (GET, POST, PUT, DELETE)." }
              }
            }
            """,
            TargetGraphQLOperation: DescribeApi);

        yield return new McpToolDefinition(
            Name: InvokeApi,
            Description: "Universally invokes an Autheris API endpoint with dynamic parameters and payload under full governance and audit.",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["endpoint", "method"],
              "properties": {
                "endpoint": { "type": "string", "description": "API route path (e.g. '/api/v1/catalog/datasets')." },
                "method": { "type": "string", "description": "HTTP method (GET, POST, PUT, DELETE)." },
                "parameters": { "type": "object", "description": "Optional query or route parameters." },
                "body": { "type": "object", "description": "Optional request body payload." }
              }
            }
            """,
            TargetGraphQLOperation: InvokeApi);
    }

    /// <summary>
    /// A string argument that occurs exactly once (case-insensitive).
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
