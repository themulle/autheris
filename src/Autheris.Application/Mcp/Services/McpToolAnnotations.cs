namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Autheris.Application.Mcp.Tools;

/// <summary>MCP tool behaviour hints (<c>ToolAnnotations</c>); <see cref="Destructive"/> only matters when not read-only.</summary>
public readonly record struct McpToolHints(bool ReadOnly, bool Destructive);

/// <summary>
/// Issue #706: the closed, name-keyed effect table of every built-in MCP tool, the single source of the
/// <c>readOnlyHint</c> / <c>destructiveHint</c> annotations in <c>tools/list</c>. Hints are advisory and never authorize;
/// a name that is not in the table (dynamic tools, new tools) is state-changing and destructive (fail-safe).
/// </summary>
public static class McpToolAnnotations
{
    private enum Effect
    {
        /// <summary>Emits catalog metadata or rows; never changes state.</summary>
        Read,

        /// <summary>Adds state (a pending plan, an inactive datasource) without overwriting existing state.</summary>
        Adds,

        /// <summary>Overwrites or removes existing state.</summary>
        Overwrites,

        /// <summary>Governed SQL: DML runs only with <c>WebSql:AllowDml</c> and a session that is not read-only.</summary>
        SqlStatement,

        /// <summary>Write procedures are rejected for read-only sessions only.</summary>
        Procedure,
    }

    private static readonly McpToolHints ReadOnlyHints = new(ReadOnly: true, Destructive: false);
    private static readonly McpToolHints DestructiveHints = new(ReadOnly: false, Destructive: true);

    private static readonly FrozenDictionary<string, Effect> Table = Build();

    public static bool IsClassified(string toolName) => toolName is not null && Table.ContainsKey(toolName);

    /// <param name="readOnlySession">The session is read-only; the gateway then rejects every write path.</param>
    /// <param name="sqlDmlAllowed"><c>WebSql:AllowDml</c>.</param>
    public static McpToolHints For(string toolName, bool readOnlySession, bool sqlDmlAllowed)
    {
        if (toolName is null || !Table.TryGetValue(toolName, out var effect))
        {
            return DestructiveHints;
        }

        return effect switch
        {
            Effect.Read => ReadOnlyHints,
            Effect.Adds => new McpToolHints(ReadOnly: false, Destructive: false),
            Effect.SqlStatement when readOnlySession || !sqlDmlAllowed => ReadOnlyHints,
            Effect.Procedure when readOnlySession => ReadOnlyHints,
            _ => DestructiveHints,
        };
    }

    private static FrozenDictionary<string, Effect> Build()
    {
        var map = new Dictionary<string, Effect>(StringComparer.OrdinalIgnoreCase);

        void Add(Effect effect, params string[] names)
        {
            foreach (var n in names) map.Add(n, effect);
        }

        Add(Effect.Read,
            McpDatasetTools.ListDatasets, McpDatasetTools.DescribeDataset, McpDatasetTools.SearchCatalog,
            McpDatasetTools.FindRelevantTables, McpDatasetTools.GetTableSchema, McpDatasetTools.ListDatasources,
            McpDatasetTools.GetDataLineage, McpDatasetTools.DescribeApi, McpDatasetTools.GetMyPermissions,
            McpDatasetTools.DescribeMyAccess, McpDatasetTools.QueryGraphQl, McpDatasetTools.QueryDataset,
            McpDatasetTools.SampleRows, McpSemanticTools.ListMetrics, McpSemanticTools.DescribeMetric,
            McpSemanticTools.QueryMetrics, "query_data_catalog", "get_golden_queries", "simulate_query",
            "query_customers", "query_invoices", McpAdminTools.ResolvePrincipal,
            McpDocTools.SearchDocs, McpDocTools.LookupErrorCode, McpDocTools.GetProcedure,
            McpDocTools.ListDocTopics, McpDocTools.ListApplications);

        Add(Effect.Adds, McpAdminTools.PlanAccess, McpAdminTools.PlanRowFilterEvaluation, McpAdminTools.RegisterDatasource);

        Add(Effect.Overwrites,
            McpAdminTools.ApplyAccess, McpAdminTools.ApplyRowFilterEvaluation, McpAdminTools.SetDatasetState,
            McpAdminTools.SetDatasetCatalog, McpDocTools.DepositDocumentation);

        // invoke_api reaches the same governed SQL path through POST /api/v1/statement.
        Add(Effect.SqlStatement, McpDatasetTools.QuerySql, McpDatasetTools.InvokeApi);
        Add(Effect.Procedure, McpDatasetTools.CallProcedure);

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
