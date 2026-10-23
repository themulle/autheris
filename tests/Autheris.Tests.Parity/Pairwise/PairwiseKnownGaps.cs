namespace Autheris.Tests.Parity.Pairwise;

/// <summary>
/// Known gaps of the pairwise cases (strict xfail, D-5), as rules because a rule covers many generated cells. Every id needs a draft in
/// section 5 of the plan (architecture test). 5xx and leaks are never gaps (SEC-PAR-05).
/// </summary>
public static class PairwiseKnownGaps
{
    private static readonly (string Name, Func<PairwiseCase, string, bool> Matches, string GapId)[] Rules =
    [
    ];

    public static IReadOnlyList<string> GapIds => Rules.Select(r => r.GapId).Distinct().ToList();

    public static string? For(PairwiseCase c, string channel) => Rules.FirstOrDefault(r => r.Matches(c, channel)).GapId;
}
