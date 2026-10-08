namespace TrinoSqlEngine;

using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Microsoft.Extensions.ObjectPool;

public class ParserPooledObjectPolicy : IPooledObjectPolicy<SqlBaseParser>
{
    private static readonly BailErrorStrategy BailStrategy = new();

    public SqlBaseParser Create()
    {
        var parser = new SqlBaseParser(null);
        parser.Interpreter.PredictionMode = PredictionMode.SLL;
        parser.ErrorHandler = BailStrategy;
        parser.RemoveErrorListeners();
        return parser;
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
        return true;
    }
}
