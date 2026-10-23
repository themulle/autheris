namespace Autheris.Tests.Parity.Boundary;

public enum BoundaryClass
{
    /// <summary>Runs normally; any failure is a real failure.</summary>
    None,

    /// <summary>Functional deviation tracked by a PARITY-GAP draft (strict xfail).</summary>
    Gap,

    /// <summary>Currently produces a 5xx or a leak. Never a KnownGap (SEC-PAR-05); runs in <c>Invariant_ObservedServerErrors</c> and keeps failing hard.</summary>
    ObservedServerError,
}

public sealed record BoundaryClassification(BoundaryClass Kind, string? GapId);

/// <summary>
/// Maps case ids to gap drafts (section 5 of the plan) or to the observed-5xx list. Exact ids only (strict xfail).
/// </summary>
public static class BoundaryKnownGaps
{
    private static readonly Dictionary<string, BoundaryClassification> Map = Build();

    public static BoundaryClassification Classify(string channel, string caseId) =>
        Map.TryGetValue($"{channel}|{caseId}", out var c) ? c : new BoundaryClassification(BoundaryClass.None, null);

    private static Dictionary<string, BoundaryClassification> Build()
    {
        var map = new Dictionary<string, BoundaryClassification>(StringComparer.Ordinal);
        foreach (var (kind, gapId, channel, ids) in BoundaryKnownGapData.Groups)
        {
            foreach (var id in ids)
            {
                if (!map.TryAdd($"{channel}|{id}", new BoundaryClassification(kind, gapId)))
                {
                    throw new InvalidOperationException($"Case {channel}|{id} is classified twice.");
                }
            }
        }

        return map;
    }
}
