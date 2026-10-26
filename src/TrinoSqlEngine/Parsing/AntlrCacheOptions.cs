namespace TrinoSqlEngine.Parsing;

/// <summary>
/// ARCH-MNT-15: bounds of the ANTLR DFA and prediction-context caches. The caches are replaced by a fresh generation when a generation holds
/// more than <see cref="MaxStatesPerGeneration"/> states (parser plus lexer DFA states plus prediction-context entries).
/// </summary>
/// <remarks>
/// Peak memory is at most about two generations plus the generations that in-flight parses still reference, because a parse that grows past the cap
/// finishes on its generation and the swap happens afterwards. There is no "0 means unlimited": every value must be inside its range.
/// </remarks>
public sealed record AntlrCacheOptions
{
    /// <summary>Largest accepted <see cref="MaxStatesPerGeneration"/> (a state costs a few KB, so this is already a multi-GB ceiling).</summary>
    public const int MaxAllowedStatesPerGeneration = 20_000_000;

    /// <summary>Largest accepted <see cref="GenerationCheckInterval"/> and <see cref="MinParsesPerGeneration"/>.</summary>
    public const int MaxAllowedCount = 100_000_000;

    /// <summary>
    /// Total state cap per generation. Conservative default (about 100 to 250 MB per generation at a few KB per state); calibration against the
    /// fuzz and SQL corpus (plan ARCH-MNT-15 Stage 0, PR #360) is a follow-up and must not raise it without a measurement.
    /// </summary>
    public int MaxStatesPerGeneration { get; init; } = 50_000;

    /// <summary>The state count is taken on every n-th use of a generation, never per token.</summary>
    public int GenerationCheckInterval { get; init; } = 32;

    /// <summary>A generation is replaced only after this many uses, so no input can force a swap per request (SC-10).</summary>
    public int MinParsesPerGeneration { get; init; } = 2_000;

    /// <summary>Empty when valid; otherwise one message per violated bound (names only, never values).</summary>
    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();
        Check(MaxStatesPerGeneration, MaxAllowedStatesPerGeneration, nameof(MaxStatesPerGeneration), failures);
        Check(GenerationCheckInterval, MaxAllowedCount, nameof(GenerationCheckInterval), failures);
        Check(MinParsesPerGeneration, MaxAllowedCount, nameof(MinParsesPerGeneration), failures);
        return failures;
    }

    private static void Check(int value, int max, string name, List<string> failures)
    {
        if (value < 1 || value > max)
        {
            failures.Add($"{name} must be between 1 and {max}.");
        }
    }
}
