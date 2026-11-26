namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Per-call ambient signal that tells the guardrail whether a tool result carries policy-masked values. Producers report from the
/// governed result models before serialization; only the guardrail opens a scope (<see cref="Begin"/>). The holder is created per call and
/// set-only, so a report can only raise the outcome and never clears or replaces it.
/// </summary>
public static class McpResultMasking
{
    private static readonly AsyncLocal<Holder?> Current = new();

    private static readonly FrozenSet<string> DataTools = new[]
    {
        McpDatasetTools.QuerySql, McpDatasetTools.QueryDataset, McpDatasetTools.QueryGraphQl, McpDatasetTools.SampleRows,
        McpDatasetTools.CallProcedure, McpDatasetTools.InvokeApi, McpSemanticTools.QueryMetrics,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Curated tools backed by a GraphQL operation or table; they report from the GraphQL result or the table fast path.</summary>
    private static readonly FrozenSet<string> CuratedDataTools = new[]
    {
        "query_customers", "query_invoices", "query_data_catalog",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> MetadataTools = new[]
    {
        McpDatasetTools.ListDatasets, McpDatasetTools.DescribeDataset, McpDatasetTools.DescribeMyAccess, McpDatasetTools.RequestAccess,
        McpDatasetTools.SearchCatalog, McpDatasetTools.FindRelevantTables, McpDatasetTools.GetTableSchema, McpDatasetTools.GetMyPermissions,
        McpDatasetTools.ListDatasources, McpDatasetTools.ListContainers, McpDatasetTools.GetDataLineage, McpDatasetTools.DescribeApi,
        McpSemanticTools.ListMetrics, McpSemanticTools.DescribeMetric, "simulate_query", "get_golden_queries",
        McpDocTools.SearchDocs, McpDocTools.LookupErrorCode, McpDocTools.GetProcedure, McpDocTools.ListDocTopics,
        McpDocTools.ListApplications, McpDocTools.DepositDocumentation, McpDocTools.ReadDocument,
        Autheris.Application.Mcp.Tools.McpAdminTools.PlanAccess, Autheris.Application.Mcp.Tools.McpAdminTools.ApplyAccess, Autheris.Application.Mcp.Tools.McpAdminTools.RegisterDatasource,
        Autheris.Application.Mcp.Tools.McpAdminTools.SetDatasetState, Autheris.Application.Mcp.Tools.McpAdminTools.ResolvePrincipal, Autheris.Application.Mcp.Tools.McpAdminTools.SetDatasetCatalog,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens a scope for one tool call. Synchronous on purpose, so the holder is set in the caller's execution context.</summary>
    public static McpResultMaskingScope Begin()
    {
        var previous = Current.Value;
        var holder = new Holder();
        Current.Value = holder;
        return new McpResultMaskingScope(holder, previous);
    }

    /// <summary>Records the outcome of a producer. A no-op when no scope is open.</summary>
    public static void Report(bool masked)
    {
        var holder = Current.Value;
        if (holder is null)
        {
            return;
        }

        Volatile.Write(ref holder.Stated, 1);
        if (masked)
        {
            Volatile.Write(ref holder.Masked, 1);
        }
    }

    /// <summary>True when the tool returns governed data, so an unreported outcome counts as masked (fail toward true).</summary>
    public static bool IsGovernedDataTool(McpToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (DataTools.Contains(tool.Name) || CuratedDataTools.Contains(tool.Name))
        {
            return true;
        }

        if (MetadataTools.Contains(tool.Name))
        {
            return false;
        }

        // An unclassified tool is treated as data.
        return true;
    }

    /// <summary>True when the tool name is classified explicitly as a data, curated data or metadata tool.</summary>
    public static bool IsClassified(string toolName) =>
        toolName is not null && (DataTools.Contains(toolName) || CuratedDataTools.Contains(toolName) || MetadataTools.Contains(toolName));

    /// <summary>
    /// True when the result has at least one row and the returned projection contains a policy-masked column. Without table metadata a
    /// restricted-scope caller cannot be resolved column by column, so that counts as masked.
    /// </summary>
    public static bool HasMaskedValues(
        TableAccessDecision decision,
        TableMetadata? metadata,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return false;
        }

        if (metadata is null && decision.ScopeClearance is not null)
        {
            return true;
        }

        // Every non-Clear level (including Deny) deliberately counts as masked, so an unknown level fails toward true.
        foreach (var key in rows[0].Keys)
        {
            if (decision.ColumnMasks?.HasMask(key) == true
                || decision.GetEffectiveColumnAccess(key, metadata) != ColumnAccessLevel.Clear)
            {
                return true;
            }
        }

        return false;
    }

    internal static void Restore(Holder? previous) => Current.Value = previous;

    internal sealed class Holder
    {
        public int Stated;
        public int Masked;
    }
}

/// <summary>The scope of one tool call. <see cref="Outcome"/> is null when no producer stated an outcome.</summary>
public sealed class McpResultMaskingScope : IDisposable
{
    private readonly McpResultMasking.Holder _holder;
    private readonly McpResultMasking.Holder? _previous;
    private int _disposed;

    internal McpResultMaskingScope(McpResultMasking.Holder holder, McpResultMasking.Holder? previous)
    {
        _holder = holder;
        _previous = previous;
    }

    public bool? Outcome => Volatile.Read(ref _holder.Stated) == 0 ? null : Volatile.Read(ref _holder.Masked) == 1;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            McpResultMasking.Restore(_previous);
        }
    }
}
