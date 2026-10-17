namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

/// <summary>Per-statement collector of <see cref="RowFilterKeySetOutcome"/> (single statement, single assembly; not thread-safe).</summary>
public sealed class RowFilterKeySetReport
{
    private readonly List<RowFilterKeySetOutcome> _outcomes = [];

    public IReadOnlyList<RowFilterKeySetOutcome> Outcomes => _outcomes;

    public void Add(RowFilterKeySetOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        _outcomes.Add(outcome);
    }

    /// <summary>True when at least one site was substituted (<see cref="KeySetOutcomeKind.Hit"/> or <see cref="KeySetOutcomeKind.Miss"/>).</summary>
    public bool HasSubstitution => _outcomes.Exists(o => o.Outcome is KeySetOutcomeKind.Hit or KeySetOutcomeKind.Miss);

    /// <summary>
    /// D-WPC-4: replaces the outcome of every substituted site by a <see cref="KeySetOutcomeKind.Fallback"/> with <paramref name="reason"/> after the
    /// one-time retry (<see cref="KeySetFallbackReason.BindLimitRetry"/> or <see cref="KeySetFallbackReason.ExpansionLimitRetry"/>); counts and ages are dropped.
    /// </summary>
    public void MarkSubstitutedSitesAsRetried(KeySetFallbackReason reason)
    {
        for (int i = 0; i < _outcomes.Count; i++)
        {
            var o = _outcomes[i];
            if (o.Outcome is KeySetOutcomeKind.Hit or KeySetOutcomeKind.Miss)
            {
                _outcomes[i] = o with { Outcome = KeySetOutcomeKind.Fallback, Reason = reason, KeyCount = null, PaddedCount = null, AgeSeconds = null };
            }
        }
    }
}
