#pragma warning disable CA1708 // adding a member to the generated partial classes makes the analyzer compare the generated names (case-only differences)
namespace TrinoSqlEngine;

using Antlr4.Runtime.Atn;
using TrinoSqlEngine.Parsing;

// ARCH-MNT-15: binds a generated recognizer to an ANTLR cache generation. The simulator setter is protected, so this has to live in the class.
// Called by AntlrCacheGenerations only.
public partial class SqlBaseLexer
{
    internal AntlrCacheGeneration? CacheGeneration { get; private set; }

    internal void BindGeneration(AntlrCacheGeneration generation)
    {
        Interpreter = new LexerATNSimulator(this, _ATN, generation.LexerDfa, generation.LexerContexts);
        CacheGeneration = generation;
    }
}

public partial class SqlBaseParser
{
    internal AntlrCacheGeneration? CacheGeneration { get; private set; }

    internal void BindGeneration(AntlrCacheGeneration generation)
    {
        Interpreter = new ParserATNSimulator(this, _ATN, generation.ParserDfa, generation.ParserContexts);
        Interpreter.PredictionMode = PredictionMode.SLL;
        CacheGeneration = generation;
    }
}
