namespace TrinoSqlEngine;

using System;
using Antlr4.Runtime.Misc;

/// <summary>
/// Thrown when SQL syntax analysis fails with a known source position (1-based line and column).
/// </summary>
public sealed class SqlSyntaxErrorException : ParseCanceledException
{
    public int Line { get; }
    public int Column { get; }

    public SqlSyntaxErrorException(int line, int column, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Line = line;
        Column = column;
    }
}
