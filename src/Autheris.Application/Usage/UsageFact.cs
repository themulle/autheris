namespace Autheris.Application.Usage;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;

public readonly record struct UsageFact(
    TableIdentifier Table,
    string DataSource,
    UsageOutcome Outcome,
    long Rows,
    bool RowsMeasured,
    long AnalysisCost,
    IReadOnlyList<GrantRef> Grants,
    SourceExecutionStats Source)
{
    public UsageFact MergeWith(in UsageFact other)
    {
        var mergedGrants = CombineGrants(Grants, other.Grants);
        var mergedSource = CombineSource(Source, other.Source);
        var outcome = (Outcome == UsageOutcome.Allow && other.Outcome != UsageOutcome.Allow)
            ? other.Outcome
            : Outcome;

        return new UsageFact(
            Table: Table,
            DataSource: string.IsNullOrEmpty(DataSource) ? other.DataSource : DataSource,
            Outcome: outcome,
            Rows: Rows + other.Rows,
            RowsMeasured: RowsMeasured || other.RowsMeasured,
            AnalysisCost: AnalysisCost + other.AnalysisCost,
            Grants: mergedGrants,
            Source: mergedSource);
    }

    private static IReadOnlyList<GrantRef> CombineGrants(IReadOnlyList<GrantRef>? first, IReadOnlyList<GrantRef>? second)
    {
        if (first == null || first.Count == 0) return second ?? Array.Empty<GrantRef>();
        if (second == null || second.Count == 0) return first;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<GrantRef>(first.Count + second.Count);

        foreach (var g in first)
        {
            if (set.Add(g.GrantId))
            {
                list.Add(g);
            }
        }

        foreach (var g in second)
        {
            if (set.Add(g.GrantId))
            {
                list.Add(g);
            }
        }

        return list;
    }

    private static SourceExecutionStats CombineSource(in SourceExecutionStats first, in SourceExecutionStats second)
    {
        long? computeMs = (first.SourceComputeMs.HasValue || second.SourceComputeMs.HasValue)
            ? (first.SourceComputeMs ?? 0) + (second.SourceComputeMs ?? 0)
            : null;

        decimal? cost = (first.SourceCost.HasValue || second.SourceCost.HasValue)
            ? (first.SourceCost ?? 0) + (second.SourceCost ?? 0)
            : null;

        string? costUnit = first.SourceCostUnit ?? second.SourceCostUnit;
        string? queryId = first.SourceQueryId ?? second.SourceQueryId;

        return new SourceExecutionStats(computeMs, cost, costUnit, queryId);
    }
}
