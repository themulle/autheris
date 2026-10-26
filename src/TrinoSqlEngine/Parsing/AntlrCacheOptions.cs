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
    public const int MaxAllowedStatesPerGeneration = 1_000_000;

    /// <summary>Largest accepted <see cref="GenerationCheckInterval"/> and <see cref="MinParsesPerGeneration"/>.</summary>
    public const int MaxAllowedCount = 1_000_000;

    /// <summary>Smallest accepted <see cref="HardCapFactor"/>.</summary>
    public const int MinHardCapFactor = 2;

    /// <summary>Largest accepted <see cref="HardCapFactor"/>.</summary>
    public const int MaxHardCapFactor = 16;

    /// <summary>
    /// Total state cap per generation. Conservative default (about 100 to 250 MB per generation at a few KB per state); calibration against the
    /// fuzz and SQL corpus (plan ARCH-MNT-15 Stage 0, PR #360) is a follow-up and must not raise it without a measurement.
    /// </summary>
    public int MaxStatesPerGeneration { get; init; } = 50_000;

    /// <summary>
    /// A generation is replaced regardless of <see cref="MinParsesPerGeneration"/> once it holds more than <see cref="HardCapFactor"/> times
    /// <see cref="MaxStatesPerGeneration"/> states. The check runs every <see cref="GenerationCheckInterval"/> uses, so a generation can overshoot the
    /// ceiling by the growth of up to one interval (32 parses by default) before it is replaced.
    /// </summary>
    public int HardCapFactor { get; init; } = 4;

    /// <summary>The state count is taken on every n-th use of a generation, never per token.</summary>
    public int GenerationCheckInterval { get; init; } = 32;

    /// <summary>A generation is replaced only after this many uses, so no input can force a swap per request (SC-10).</summary>
    public int MinParsesPerGeneration { get; init; } = 2_000;

    /// <summary>Empty when valid; otherwise one message per violated bound (names only, never values).</summary>
    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();
        Check(MaxStatesPerGeneration, 1, MaxAllowedStatesPerGeneration, nameof(MaxStatesPerGeneration), failures);
        Check(GenerationCheckInterval, 1, MaxAllowedCount, nameof(GenerationCheckInterval), failures);
        Check(MinParsesPerGeneration, 1, MaxAllowedCount, nameof(MinParsesPerGeneration), failures);
        Check(HardCapFactor, MinHardCapFactor, MaxHardCapFactor, nameof(HardCapFactor), failures);
        return failures;
    }

    private static void Check(int value, int min, int max, string name, List<string> failures)
    {
        if (value < min || value > max)
        {
            failures.Add($"{name} must be between {min} and {max}.");
        }
    }
}
