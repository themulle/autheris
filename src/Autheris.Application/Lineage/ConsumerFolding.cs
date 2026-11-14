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
    /// Groups daily consumer summaries by token-derived clientId when present, falling back to the client-asserted
    /// application label when no clientId exists. Prevents different clientIds or unauthenticated callers from
    /// merging into another application's row, while folding multiple asserted labels under the same clientId.
    /// </summary>
    public static IReadOnlyList<ConsumerAppDailySummary> GroupByClient(IEnumerable<ConsumerAppDailySummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);

        var list = summaries.ToList();
        if (list.Count == 0)
        {
            return Array.Empty<ConsumerAppDailySummary>();
        }

        var grouped = new List<ConsumerAppDailySummary>();
        foreach (var group in list.GroupBy(GetClientGroupKey))
        {
            var cells = group.ToList();
            string? clientId = NormalizeClientId(cells.Select(c => c.ClientId).FirstOrDefault(HasClientId));

            var nonUnknownApps = cells
                .Select(c => c.Application)
                .Where(a => !string.IsNullOrWhiteSpace(a) && !string.Equals(a, "unknown", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            string displayApp;
            bool isClientAsserted;
            if (clientId != null)
            {
                if (nonUnknownApps.Count == 1)
                {
                    displayApp = nonUnknownApps[0];
                    isClientAsserted = cells.Any(c => ResolveIsClientAsserted(c));
                }
                else
                {
                    displayApp = clientId;
                    isClientAsserted = false;
                }
            }
            else
            {
                displayApp = cells[0].Application;
                isClientAsserted = cells.Any(c => ResolveIsClientAsserted(c));
            }

            IReadOnlySet<string>? mergedActorKeys = cells.All(c => c.ActorKeys != null)
                ? new HashSet<string>(cells.SelectMany(c => c.ActorKeys!), StringComparer.Ordinal)
                : null;
            int maxDistinct = cells.Max(c => c.DistinctActors);
            int distinctActors = mergedActorKeys != null ? Math.Max(maxDistinct, mergedActorKeys.Count) : maxDistinct;

            var versions = cells.SelectMany(c => c.Versions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var queryNames = cells.SelectMany(c => c.QueryNames).Distinct(StringComparer.Ordinal).ToList();
            var actorSids = cells.Any(c => c.ActorSids != null)
                ? cells.Where(c => c.ActorSids != null).SelectMany(c => c.ActorSids!).Distinct(StringComparer.Ordinal).ToList()
                : null;

            grouped.Add(new ConsumerAppDailySummary(
                Application: displayApp,
                Day: group.Key.Day,
                Calls: cells.Sum(c => c.Calls),
                RowsReturned: cells.Sum(c => c.RowsReturned),
                RowsMeasuredCalls: cells.Sum(c => c.RowsMeasuredCalls),
                Rejections: new RejectionBreakdown(
                    cells.Sum(c => c.Rejections.Access),
                    cells.Sum(c => c.Rejections.RateLimit),
                    cells.Sum(c => c.Rejections.Cost),
                    cells.Sum(c => c.Rejections.Other)),
                Versions: versions,
                QueryNames: queryNames,
                DistinctActors: distinctActors,
                ActorSids: actorSids
            )
            {
                ClientId = clientId,
                IsClientAsserted = isClientAsserted,
                ActorKeys = mergedActorKeys
            });
        }

        return grouped;
    }

    private static (string Day, string Key) GetClientGroupKey(ConsumerAppDailySummary summary)
    {
        var clientId = NormalizeClientId(summary.ClientId);
        return clientId != null
            ? (summary.Day, "client:" + clientId.ToUpperInvariant())
            : (summary.Day, "asserted:" + summary.Application);
    }

    private static bool HasClientId(string? clientId) => NormalizeClientId(clientId) != null;

    private static string? NormalizeClientId(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId) ||
            string.Equals(clientId, "unknown", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clientId, "invalid", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return clientId.Trim();
    }

    private static bool ResolveIsClientAsserted(ConsumerAppDailySummary summary)
    {
        if (summary.IsClientAsserted.HasValue)
        {
            return summary.IsClientAsserted.Value;
        }

        return !string.IsNullOrWhiteSpace(summary.Application) &&
               !string.Equals(summary.Application, "unknown", StringComparison.OrdinalIgnoreCase);
    }

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

        var list = GroupByClient(summaries);
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
                    ActorSids: null,
                    ClientId: s.ClientId,
                    IsClientAsserted: s.IsClientAsserted ?? false
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
                // 03 / Differencing Attack Defense: the union of folded actors is still < k (or unknown): suppress the counts.
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
