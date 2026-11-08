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
    public bool Tool(string name) => mcpEnabled && switches is not null && switches.IsToolEnabled(name);

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
            parts.Add("describe_dataset returns the GraphQL field, the filter and sort types and an example query for each dataset.");
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
        return "Lists the datasets (tables) you may query with their GraphQL field, and all protocols of the gateway."
            + (next.Count == 0 ? string.Empty : " Start here, then " + string.Join(" and ", next) + ".");
    }

    public string ServerInstructions()
    {
        var steps = new List<string>(3);
        var find = Plain(McpDatasetTools.FindRelevantTables, McpDatasetTools.SearchCatalog);
        if (find.Length > 0) steps.Add($"call {find} to find candidate tables and foreign key join conditions for your use-case");
        var schema = Plain(McpDatasetTools.GetTableSchema, McpDatasetTools.DescribeDataset)
            .Replace(McpDatasetTools.DescribeDataset, "describe_dataset(brief: true)", StringComparison.Ordinal);
        if (schema.Length > 0) steps.Add($"call {schema} to fetch concise schemas without consuming token budget");
        var query = Plain(McpDatasetTools.QuerySql, McpDatasetTools.QueryDataset, McpDatasetTools.QueryGraphQl);
        if (query.Length > 0) steps.Add($"query data with {query}");

        var text = "Autheris is a governed data gateway with 10,000+ datasets.";
        if (steps.Count > 0)
        {
            text += " For token-efficient AI workflows (Report & App Generation): "
                + string.Join(", ", steps.Select((s, i) => $"{i + 1}) {s}")) + ".";
        }

        if (Tool(McpDatasetTools.ListDatasets)) text += " Use list_datasets(overview: true) for domain summaries.";
        if (Tool(McpDatasetTools.SampleRows)) text += " sample_rows inspects sample records.";
        return text + " Results are filtered and masked for your identity; hidden datasets and columns do not appear.";
    }

    private string Plain(params string[] names) => JoinOr(names.Where(Tool));

    private string Quoted(params string[] names) => JoinOr(names.Where(Tool).Select(n => $"'{n}'"));

    private static string JoinOr(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count switch { 0 => string.Empty, 1 => list[0], _ => string.Join(", ", list[..^1]) + " or " + list[^1] };
    }
}
