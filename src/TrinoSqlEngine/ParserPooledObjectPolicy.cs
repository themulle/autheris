namespace TrinoSqlEngine;

using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Microsoft.Extensions.ObjectPool;
using TrinoSqlEngine.Parsing;

public class ParserPooledObjectPolicy : IPooledObjectPolicy<SqlBaseParser>
{
    private static readonly BailErrorStrategy BailStrategy = new();

    private readonly AntlrCacheGenerations _generations;

    public ParserPooledObjectPolicy()
        : this(AntlrCacheGenerations.Shared)
    {
    }

    public ParserPooledObjectPolicy(AntlrCacheGenerations generations)
    {
        ArgumentNullException.ThrowIfNull(generations);
        _generations = generations;
    }

    public SqlBaseParser Create()
    {
        var parser = _generations.CreateParser();
        Configure(parser);
        return parser;
    }

    /// <summary>
    /// Called after a parser was taken from the pool: a parser bound to an older generation is rebuilt on the current one, with the same
    /// SLL mode, error strategy and listeners as <see cref="Create"/> (ARCH-MNT-15-SC-08).
    /// </summary>
    public void PrepareForUse(SqlBaseParser parser)
    {
        if (ReferenceEquals(parser.CacheGeneration, _generations.Current))
        {
            return;
        }

        _generations.Rebind(parser);
        Configure(parser);
        parser.Reset();
    }

    public bool Return(SqlBaseParser parser)
    {
        parser.Reset();
        if (parser.Interpreter.PredictionMode != PredictionMode.SLL)
        {
            parser.Interpreter.PredictionMode = PredictionMode.SLL;
        }
        if (parser.ErrorHandler != BailStrategy)
        {
            parser.ErrorHandler = BailStrategy;
        }
        parser.RemoveErrorListeners();
        parser.TokenStream = null;

        // Counts the use and may swap the generation; a failure throws (the parse is rejected, the instance is not pooled).
        _generations.Release(parser);

        // A parser of an older generation is dropped so that the pool does not keep that generation alive.
        return ReferenceEquals(parser.CacheGeneration, _generations.Current);
    }

    private static void Configure(SqlBaseParser parser)
    {
        parser.Interpreter.PredictionMode = PredictionMode.SLL;
        parser.ErrorHandler = BailStrategy;
        parser.RemoveErrorListeners();
    }
}
