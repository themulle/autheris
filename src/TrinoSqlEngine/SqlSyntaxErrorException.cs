namespace TrinoSqlEngine;

using System;
using Antlr4.Runtime.Misc;

/// <summary>Why the lexer or parser refused a statement; <see cref="Syntax"/> is the default.</summary>
public enum SqlSyntaxRejection
{
    Syntax = 0,
    Comment = 1,
    NestedComment = 2
}

/// <summary>
/// Thrown when SQL syntax analysis fails with a known source position (1-based line and column).
/// </summary>
public sealed class SqlSyntaxErrorException : ParseCanceledException
{
    public int Line { get; }
    public int Column { get; }

    /// <summary>The kind of refusal; a comment refusal never carries the comment text.</summary>
    public SqlSyntaxRejection Rejection { get; init; }

    public SqlSyntaxErrorException(int line, int column, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Line = line;
        Column = column;
    }
}
