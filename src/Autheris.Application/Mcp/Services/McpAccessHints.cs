namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Mcp.Interfaces;

/// <summary>
/// The access hints a client reads (catalog guidance, endpoint and dataset access paths, the
/// <c>list_datasets</c> description, the server instructions). Reads only the startup switches, names a tool only when
/// its switch is on (null switches fail closed) and never names a disabled feature. Stateless: no cache
/// is shared between instances. The switches are startup-only, so a hint built at startup cannot go stale; execution
/// enforces <see cref="IReadPathSwitches.IsToolEnabled"/> independently and a hint never grants access.
/// </summary>
internal sealed class McpAccessHints(IReadPathSwitches? switches, bool mcpEnabled = true)
{
    public bool Tool(string name) =>
        mcpEnabled && switches is not null && switches.IsToolEnabled(name)
        && (name != McpDatasetTools.QueryDataset || switches.RestDataEndpointEnabled);

    /// <summary>Null when nothing it would recommend is enabled (never a default string).</summary>
    public string? Guidance()
    {
        var parts = new List<string>(3);
        if (Tool(McpDatasetTools.QueryGraphQl))
        {
            parts.Add("Query data with GraphQL: call the query_graphql tool (or POST the query to the GraphQL endpoint).");
        }

        if (Tool(McpDatasetTools.DescribeDataset))
        {
            parts.Add("describe_dataset returns the GraphQL field, the filter and sort types and an example query for each dataset (not with brief: true).");
        }

        if (!mcpEnabled)
        {
            // REST catalog without MCP: no tool is named, the mapped GraphQL route is still truthful.
            parts.Add("Query data with GraphQL: POST the query to the GraphQL endpoint.");
        }

        if (parts.Count > 0)
        {
            parts.Add("The other endpoints are listed for clients that need a specific protocol.");
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>Discovery and schema tools named in the catalog tips; empty when none is enabled.</summary>
    public string Tip(int total)
    {
        var find = Quoted(McpDatasetTools.FindRelevantTables, McpDatasetTools.SearchCatalog);
        var schema = Quoted(McpDatasetTools.GetTableSchema, McpDatasetTools.DescribeDataset);
        if (find.Length == 0 && schema.Length == 0)
        {
            return string.Empty;
        }

        var sentence = new List<string>(2);
        if (find.Length > 0) sentence.Add($"use {find} for natural language discovery");
        if (schema.Length > 0) sentence.Add($"use {schema} for concise schemas");
        return $" [Tip: Catalog contains {total} datasets. For token-efficient querying, {string.Join(", and ", sentence)}.]";
    }

    /// <summary>The overview tail: other discovery tools, only the enabled ones.</summary>
    public string OverviewGuidance(int tables, int domains)
    {
        var find = Quoted(McpDatasetTools.FindRelevantTables, McpDatasetTools.SearchCatalog);
        var text = $"Catalog contains {tables} tables across {domains} domains. Use 'domain' parameter in list_datasets";
        return find.Length == 0 ? text + "." : $"{text}, or use {find} for natural language table discovery.";
    }

    public string GraphQlEndpointDescription() =>
        "Preferred query path: query, filter, sort, page and follow relations between datasets in one request." +
        (Tool(McpDatasetTools.QueryGraphQl) ? " Use the query_graphql tool." : string.Empty);

    public string ListDatasetsDescription()
    {
        var next = new List<string>(2);
        if (Tool(McpDatasetTools.DescribeDataset)) next.Add("call describe_dataset");
        if (Tool(McpDatasetTools.QueryGraphQl)) next.Add("query with query_graphql");
        return "Lists the datasets (tables) you may query with their GraphQL field, and all protocols of the gateway (format 'compact' leaves out the GraphQL field and the protocols)."
            + (next.Count == 0 ? string.Empty : " Start here, then " + string.Join(" and ", next) + ".");
    }

    public string ServerInstructions()
    {
        var text = "Autheris is a governed data gateway. Results are filtered and masked for your identity; hidden datasets and columns do not appear.";
        var steps = new List<string>(3);
        var find = Plain(McpDatasetTools.FindRelevantTables, McpDatasetTools.SearchCatalog);
        if (find.Length > 0) steps.Add($"call {find} to find candidate tables and foreign key join conditions");
        if (Tool(McpDatasetTools.DescribeDataset))
        {
            steps.Add("call describe_dataset (without brief) for the columns, "
                + (Tool(McpDatasetTools.QueryGraphQl) ? "GraphQL field, filter and sort types, relations and an example query" : "relations and an example query")
                + " before you write a query");
        }
        else if (Tool(McpDatasetTools.GetTableSchema))
        {
            steps.Add("call get_table_schema for the columns");
        }

        var queryRule = QueryToolRule();
        if (queryRule.Length > 0) steps.Add("query the data");
        if (steps.Count > 0)
        {
            text += " Workflow: " + string.Join("; ", steps.Select((s, i) => $"{i + 1}) {s}")) + ".";
        }

        if (queryRule.Length > 0) text += "\n" + queryRule;
        if (mcpEnabled && switches is not null && switches.IsResourceEnabled(McpResourceProvider.QueryGuideDocUri))
        {
            text += $"\nBefore writing a non-trivial query, read the resource {McpResourceProvider.QueryGuideDocUri}.";
        }

        var tail = new List<string>(2);
        if (Tool(McpDatasetTools.ListDatasets)) tail.Add("Use list_datasets(overview: true) for domain summaries.");
        if (Tool(McpDatasetTools.SampleRows)) tail.Add("sample_rows inspects sample records.");
        return tail.Count == 0 ? text : text + "\n" + string.Join(' ', tail);
    }

    /// <summary>One short rule for which query tool to use when; empty when no query tool is enabled.</summary>
    private string QueryToolRule()
    {
        var graphQl = Tool(McpDatasetTools.QueryGraphQl);
        var sql = Tool(McpDatasetTools.QuerySql);
        var dataset = Tool(McpDatasetTools.QueryDataset);
        var count = (graphQl ? 1 : 0) + (sql ? 1 : 0) + (dataset ? 1 : 0);
        if (count == 0) return string.Empty;
        if (count == 1) return $"Query data with {(graphQl ? McpDatasetTools.QueryGraphQl : sql ? McpDatasetTools.QuerySql : McpDatasetTools.QueryDataset)}.";

        var parts = new List<string>(3);
        if (graphQl) parts.Add("query_graphql is the default for reading data (filter, sort, page, aggregate, follow relations between datasets)");
        if (sql) parts.Add("use query_sql when you need SQL" + (graphQl ? " or a query GraphQL cannot express" : string.Empty));
        if (dataset) parts.Add("use query_dataset for a simple read of one dataset by identifier");
        return "Which query tool: " + string.Join("; ", parts) + ".";
    }

    /// <summary>The tip under the catalog summary; null when no discovery or schema tool is enabled.</summary>
    public string? CatalogSummaryTip()
    {
        var find = BacktickList(McpDatasetTools.FindRelevantTables, McpDatasetTools.SearchCatalog);
        string? schema = null;
        if (Tool(McpDatasetTools.DescribeDataset))
        {
            schema = $"`{McpDatasetTools.DescribeDataset}` for the columns, "
                + (Tool(McpDatasetTools.QueryGraphQl) ? "GraphQL field and relations" : "relations and an example query");
        }
        else if (Tool(McpDatasetTools.GetTableSchema))
        {
            schema = $"`{McpDatasetTools.GetTableSchema}` for the columns";
        }

        if (find.Length == 0 && schema is null) return null;
        var text = find.Length > 0 ? $"Use {find} to find tables for a use case" : "Use";
        if (schema is not null) text += find.Length > 0 ? $", and {schema} before you query" : $" {schema} before you query";
        return $"> **Tip:** {text}.";
    }

    /// <summary>Text of the <c>explore_dataset</c> prompt; names only enabled tools.</summary>
    public string ExplorePromptText(string ds)
    {
        var steps = new List<string>(3);
        var inspect = Plain(McpDatasetTools.DescribeDataset, McpDatasetTools.GetMyPermissions);
        if (inspect.Length > 0) steps.Add($"Call {inspect} for '{ds}' to view accessible columns, data types, and masking rules.");
        if (Tool(McpDatasetTools.SampleRows)) steps.Add("Call sample_rows with count=5 to inspect representative records.");
        var query = Plain(McpDatasetTools.QueryGraphQl, McpDatasetTools.QuerySql, McpDatasetTools.QueryDataset);
        if (query.Length > 0)
        {
            var step = $"Recommend the best query strategy ({query}) including pagination and filter criteria.";
            if (mcpEnabled && switches is not null && switches.IsResourceEnabled(McpResourceProvider.QueryGuideDocUri))
            {
                step += $" Read {McpResourceProvider.QueryGuideDocUri} for the query dialect.";
            }

            steps.Add(step);
        }

        return NumberedPrompt($"I need to explore the dataset '{ds}'.", steps);
    }

    /// <summary>Text of the <c>audit_access_compliance</c> prompt; names only enabled tools.</summary>
    public string AuditPromptText(string ds)
    {
        var steps = new List<string>(3)
        {
            "Check effective authorization and row-level security constraints.",
            "List all columns with their sensitivity classifications and masking states (clear, mask, deny)."
        };
        if (Tool(McpDatasetTools.GetDataLineage))
        {
            steps.Add("Inspect data lineage via get_data_lineage to identify downstream consumers and potential compliance risks.");
        }

        return NumberedPrompt($"Perform an access and compliance audit on dataset '{ds}' for the current caller.", steps);
    }

    public string QueryGraphQlDescription() =>
        "Default tool for reading data. Runs a read-only GraphQL query against the gateway: filter (where), sort (orderBy), page (first, offset) and follow relations between datasets."
        + (Tool(McpDatasetTools.DescribeDataset) ? " Get the field names and an example query from describe_dataset." : string.Empty);

    private static string NumberedPrompt(string intro, List<string> steps) =>
        steps.Count == 0
            ? intro
            : intro[..^1] + ". Please:\n" + string.Join('\n', steps.Select((s, i) => $"{i + 1}. {s}"));

    private string BacktickList(params string[] names) => JoinOr(names.Where(Tool).Select(n => $"`{n}`"));

    private string Plain(params string[] names) => JoinOr(names.Where(Tool));

    private string Quoted(params string[] names) => JoinOr(names.Where(Tool).Select(n => $"'{n}'"));

    private static string JoinOr(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count switch { 0 => string.Empty, 1 => list[0], _ => string.Join(", ", list[..^1]) + " or " + list[^1] };
    }
}
