namespace TrinoSqlEngine;

using System;
using Antlr4.Runtime.Misc;

/// <summary>
/// #296 (SEC-PG-08): marker cause of a rejected literal. A string literal (or quoted identifier) contains a character sequence that the
/// lexer hardening rejects (SQ-01 backslash escapes, SQL-1 <c>[</c>, <c>]</c>, <c>--</c>, <c>/*</c>). The check sites throw a
/// <see cref="ParseCanceledException"/> exactly as before (callers and tests that match that type keep working) and carry this exception as
/// its inner exception; the error mapper matches this type only and reports the documented <c>SQL_LITERAL_NOT_PERMITTED</c>. The message is
/// a fixed text: it never echoes the literal or the matched sequence.
/// </summary>
public sealed class LiteralTokenRejectedException()
    : Exception("A string literal contains a character sequence that is not permitted; pass the value as a parameter.")
{
    /// <summary>The exception the check sites throw: the parse rejection with the position and a fixed reason, caused by this marker.</summary>
    internal static ParseCanceledException Reject(int line, int column, string reason) =>
        new($"line {line}:{column}: {reason}", new LiteralTokenRejectedException());
}
