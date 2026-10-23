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

    /// <summary>
    /// MISC-17: k-anonymity is decided per published cell on that cell's own distinct actors (never on a window-wide per-application count).
    /// Cells below <paramref name="minActors"/> fold into one "Other" bucket per day; the folded bucket counts the true union of the folded
    /// cells' actors (<see cref="ConsumerAppDailySummary.ActorKeys"/>) and is checked against k independently. A folded cell without actor
    /// identities suppresses its day's bucket (fail closed).
    /// </summary>
    public static (IReadOnlyList<CallingApplicationUsage> Results, bool SuppressedSmallGroups) Fold(
        IEnumerable<ConsumerAppDailySummary> summaries,
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

        foreach (var s in list)
        {
            if (s.DistinctActors >= minActors)
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
                    DistinctActors: s.DistinctActors,
                    ActorSids: null
                ));
            }
            else
            {
                foldedSummaries.Add(s);
            }
        }

        bool suppressedSmallGroups = false;

        // One "Other" bucket per day, each checked against k independently (a bucket is never published under another day).
        foreach (var dayGroup in foldedSummaries.GroupBy(x => x.Day, StringComparer.Ordinal))
        {
            var cells = dayGroup.ToList();
            var identified = cells.All(c => c.ActorKeys != null);
            int unionActors = identified ? cells.SelectMany(c => c.ActorKeys!).Distinct(StringComparer.Ordinal).Count() : 0;

            if (!identified || unionActors < minActors)
            {
                // SEC-AUD114-03 / Differencing Attack Defense: the union of folded actors is still < k (or unknown): suppress the counts.
                suppressedSmallGroups = true;
                keptUsages.Add(new CallingApplicationUsage(
                    Application: otherBucketName,
                    Day: dayGroup.Key,
                    Calls: 0,
                    RowsReturned: 0,
                    RowsMeasuredCalls: 0,
                    Rejections: new RejectionBreakdown(0, 0, 0, 0),
                    Versions: Array.Empty<string>(),
                    QueryNames: Array.Empty<string>(),
                    DistinctActors: unionActors,
                    ActorSids: null
                ));
                continue;
            }

            keptUsages.Add(new CallingApplicationUsage(
                Application: otherBucketName,
                Day: dayGroup.Key,
                Calls: cells.Sum(x => x.Calls),
                RowsReturned: cells.Sum(x => x.RowsReturned),
                RowsMeasuredCalls: cells.Sum(x => x.RowsMeasuredCalls),
                Rejections: new RejectionBreakdown(
                    cells.Sum(x => x.Rejections.Access),
                    cells.Sum(x => x.Rejections.RateLimit),
                    cells.Sum(x => x.Rejections.Cost),
                    cells.Sum(x => x.Rejections.Other)),
                Versions: cells.SelectMany(x => x.Versions).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                QueryNames: Array.Empty<string>(),
                DistinctActors: unionActors,
                ActorSids: null
            ));
        }

        return (keptUsages, suppressedSmallGroups);
    }
}
