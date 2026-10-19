namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;

/// <summary>PLAN-SEC-115: the four switchable MCP tool groups. <see cref="None"/> tools are controlled by <c>Mcp:DisabledTools</c> only.</summary>
public enum McpToolGroup
{
    None = 0,
    Catalog,
    Query,
    Api,
    Access
}

/// <summary>
/// PLAN-SEC-115 A-2: the closed, name-keyed group table of every built-in MCP tool (case-insensitive, like the registry). A name
/// that is not in the table is a dynamic tool (GraphQL discovery, <c>Mcp:AllowedOperations</c>) and belongs to <see cref="McpToolGroup.Query"/>,
/// so it is never "ungrouped and always on". The completeness tests fail CI when the registry holds a tool that is not classified here.
/// </summary>
public static class McpToolGroups
{
    private static readonly FrozenDictionary<string, McpToolGroup> Table = Build();

    /// <summary>Every built-in tool name, including the ungrouped ones.</summary>
    public static IReadOnlyCollection<string> BuiltInNames { get; } = Table.Keys;

    /// <summary>Group of a tool name. Unknown names (dynamic tools) resolve to <see cref="McpToolGroup.Query"/> (fail-safe).</summary>
    public static McpToolGroup Of(string toolName) =>
        toolName is not null && Table.TryGetValue(toolName, out var group) ? group : McpToolGroup.Query;

    public static bool IsKnownBuiltIn(string toolName) => toolName is not null && Table.ContainsKey(toolName);

    private static FrozenDictionary<string, McpToolGroup> Build()
    {
        var map = new Dictionary<string, McpToolGroup>(StringComparer.OrdinalIgnoreCase);

        void Add(McpToolGroup group, params string[] names)
        {
            foreach (var n in names) map[n] = group;
        }

        Add(McpToolGroup.Catalog,
            McpDatasetTools.ListDatasets, McpDatasetTools.DescribeDataset, McpDatasetTools.SearchCatalog,
            McpDatasetTools.ListDatasources, McpDatasetTools.GetDataLineage, "query_data_catalog",
            McpSemanticTools.ListMetrics, McpSemanticTools.DescribeMetric, "get_golden_queries");

        Add(McpToolGroup.Query,
            McpDatasetTools.QueryDataset, McpDatasetTools.QuerySql, McpDatasetTools.QueryGraphQl, McpDatasetTools.SampleRows,
            McpDatasetTools.CallProcedure,
            McpSemanticTools.QueryMetrics, "simulate_query", "query_customers", "query_invoices");

        Add(McpToolGroup.Api, McpDatasetTools.InvokeApi, McpDatasetTools.DescribeApi);
        Add(McpToolGroup.Access, McpDatasetTools.GetMyPermissions, McpDatasetTools.DescribeMyAccess);

        // Ungrouped: only Mcp:DisabledTools can switch these off (admin tools are limited to the cluster admin anyway).
        Add(McpToolGroup.None,
            Autheris.Application.Mcp.Tools.McpAdminTools.PlanAccess, Autheris.Application.Mcp.Tools.McpAdminTools.ApplyAccess,
            Autheris.Application.Mcp.Tools.McpAdminTools.RegisterDatasource, Autheris.Application.Mcp.Tools.McpAdminTools.SetDatasetState,
            Autheris.Application.Mcp.Tools.McpAdminTools.ResolvePrincipal, Autheris.Application.Mcp.Tools.McpAdminTools.PlanRowFilterEvaluation,
            Autheris.Application.Mcp.Tools.McpAdminTools.ApplyRowFilterEvaluation, Autheris.Application.Mcp.Tools.McpAdminTools.SetDatasetCatalog,
            McpDocTools.SearchDocs, McpDocTools.LookupErrorCode, McpDocTools.GetProcedure,
            McpDocTools.ListDocTopics, McpDocTools.ListApplications, McpDocTools.DepositDocumentation);

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
