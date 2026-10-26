namespace Autheris.Domain.Options;

/// <summary>
/// ARCH-MNT-15: bounds of the SQL parser's ANTLR DFA caches (<c>Gateway:SqlParserCache</c>). The caches are replaced by a fresh generation once
/// a generation holds more than <see cref="MaxStatesPerGeneration"/> states. Every value must be greater than zero; there is no "unlimited".
/// </summary>
public sealed class SqlParserCacheOptions
{
    /// <summary>
    /// Total state cap per generation (parser plus lexer DFA states plus prediction-context entries). The default is calibrated: the parser corpus
    /// needs about 9,000 states, and 50,000 x <see cref="HardCapFactor"/> at about 1.3 KB per state is about 240 MB worst case.
    /// </summary>
    public int MaxStatesPerGeneration { get; set; } = 50_000;

    /// <summary>A generation is replaced regardless of the minimum parse count once it holds more than this factor times the cap (2 to 16).</summary>
    public int HardCapFactor { get; set; } = 4;

    /// <summary>The state count is taken on every n-th use of a generation.</summary>
    public int GenerationCheckInterval { get; set; } = 32;

    /// <summary>Minimum number of uses before a generation may be replaced (no swap per request).</summary>
    public int MinParsesPerGeneration { get; set; } = 2_000;
}
