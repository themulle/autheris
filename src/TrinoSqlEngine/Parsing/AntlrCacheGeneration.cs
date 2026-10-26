namespace TrinoSqlEngine.Parsing;

using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Dfa;

/// <summary>
/// One set of ANTLR caches (parser and lexer DFA arrays and prediction-context caches). ANTLR itself adds states while it predicts; nothing else
/// mutates a generation, and nothing clears one (ARCH-MNT-15-SC-02). It becomes garbage when the last parse or lexer that references it is gone.
/// </summary>
public sealed class AntlrCacheGeneration
{
    internal long UsesCounter;

    internal AntlrCacheGeneration(long number)
    {
        Number = number;
        ParserDfa = NewDfa(SqlBaseParser._ATN);
        LexerDfa = NewDfa(SqlBaseLexer._ATN);
    }

    /// <summary>Monotonic generation number (1 for the first generation).</summary>
    public long Number { get; }

    /// <summary>Number of times this generation was handed to a parser or lexer user.</summary>
    public long Uses => Volatile.Read(ref UsesCounter);

    internal DFA[] ParserDfa { get; }

    internal DFA[] LexerDfa { get; }

    internal PredictionContextCache ParserContexts { get; } = new();

    internal PredictionContextCache LexerContexts { get; } = new();

    /// <summary>Total states of the generation: parser and lexer DFA states plus both prediction-context caches (SC-03).</summary>
    public static long CountStates(AntlrCacheGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        long total = generation.ParserContexts.Count + generation.LexerContexts.Count;
        foreach (var dfa in generation.ParserDfa)
        {
            total += dfa.states.Count;
        }

        foreach (var dfa in generation.LexerDfa)
        {
            total += dfa.states.Count;
        }

        return total;
    }

    private static DFA[] NewDfa(ATN atn)
    {
        var result = new DFA[atn.NumberOfDecisions];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new DFA(atn.GetDecisionState(i), i);
        }

        return result;
    }
}
