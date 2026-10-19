namespace Autheris.Application.Lineage;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;

/// <summary>
/// Architecture Phase 2 / Issue #114: Privacy-preserving k-anonymity (k=3) folding engine for consumer telemetry.
/// Gated fail-closed: groups with fewer than k human actors are folded into an aggregate bucket ("Other / Aggregated").
/// If the combined bucket still fails to satisfy k-anonymity, counts are suppressed entirely to prevent differencing attacks.
/// </summary>
public static class ConsumerFolding
{
    public const int DefaultMinActors = 3;
    public const string DefaultOtherBucketName = "Other / Aggregated";
    public const string FallbackOtherBucketName = "other";

    public static (IReadOnlyList<CallingApplicationUsage> Results, bool SuppressedSmallGroups) Fold(
        IEnumerable<ConsumerAppDailySummary> summaries,
        IReadOnlyDictionary<string, int>? distinctActorsByApp = null,
        int minActors = DefaultMinActors,
        string otherBucketName = DefaultOtherBucketName)
    {
        ArgumentNullException.ThrowIfNull(summaries);

        var list = summaries.ToList();
        if (list.Count == 0)
        {
            return (Array.Empty<CallingApplicationUsage>(), false);
        }

        var keptUsages = new List<CallingApplicationUsage>();
        var foldedSummaries = new List<ConsumerAppDailySummary>();
        int foldedDistinctActorsTotal = 0;

        foreach (var s in list)
        {
            int actorCount = s.DistinctActors;
            if (distinctActorsByApp != null && distinctActorsByApp.TryGetValue(s.Application, out var count))
            {
                actorCount = Math.Max(actorCount, count);
            }

            if (actorCount >= minActors)
            {
                // In general view: ActorSids is null (personal SIDs strictly suppressed)
                // Specific query names are omitted
                keptUsages.Add(new CallingApplicationUsage(
                    Application: s.Application,
                    Day: s.Day,
                    Calls: s.Calls,
                    RowsReturned: s.RowsReturned,
                    RowsMeasuredCalls: s.RowsMeasuredCalls,
                    Rejections: s.Rejections,
                    Versions: s.Versions,
                    QueryNames: Array.Empty<string>(),
                    DistinctActors: actorCount,
                    ActorSids: null
                ));
            }
            else
            {
                foldedSummaries.Add(s);
                foldedDistinctActorsTotal += actorCount;
            }
        }

        bool suppressedSmallGroups = false;

        if (foldedSummaries.Count > 0)
        {
            long calls = foldedSummaries.Sum(x => x.Calls);
            long rows = foldedSummaries.Sum(x => x.RowsReturned);
            long measured = foldedSummaries.Sum(x => x.RowsMeasuredCalls);
            long acc = foldedSummaries.Sum(x => x.Rejections.Access);
            long rl = foldedSummaries.Sum(x => x.Rejections.RateLimit);
            long cost = foldedSummaries.Sum(x => x.Rejections.Cost);
            long oth = foldedSummaries.Sum(x => x.Rejections.Other);
            var versions = foldedSummaries.SelectMany(x => x.Versions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string day = foldedSummaries[0].Day;

            if (foldedDistinctActorsTotal < minActors)
            {
                // SEC-AUD114-03 / Differencing Attack Defense:
                // If total distinct actors across all folded groups is still < k, suppress metric counts completely.
                suppressedSmallGroups = true;
                keptUsages.Add(new CallingApplicationUsage(
                    Application: otherBucketName,
                    Day: day,
                    Calls: 0,
                    RowsReturned: 0,
                    RowsMeasuredCalls: 0,
                    Rejections: new RejectionBreakdown(0, 0, 0, 0),
                    Versions: Array.Empty<string>(),
                    QueryNames: Array.Empty<string>(),
                    DistinctActors: foldedDistinctActorsTotal,
                    ActorSids: null
                ));
            }
            else
            {
                keptUsages.Add(new CallingApplicationUsage(
                    Application: otherBucketName,
                    Day: day,
                    Calls: calls,
                    RowsReturned: rows,
                    RowsMeasuredCalls: measured,
                    Rejections: new RejectionBreakdown(acc, rl, cost, oth),
                    Versions: versions,
                    QueryNames: Array.Empty<string>(),
                    DistinctActors: foldedDistinctActorsTotal,
                    ActorSids: null
                ));
            }
        }

        return (keptUsages, suppressedSmallGroups);
    }
}
