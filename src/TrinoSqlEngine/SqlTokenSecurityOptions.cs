using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using Microsoft.Extensions.ObjectPool;

namespace TrinoSqlEngine;

/// <summary>
/// SEC P-04: Immutable, per-call token-level security switches. Passed explicitly to
/// <see cref="FastSqlEngine.Parse(ReadOnlyMemory{char}, SqlTokenSecurityOptions?, CancellationToken)"/> so that
/// concurrent callers sharing one engine instance can never influence each other's checks.
/// </summary>
public sealed record SqlTokenSecurityOptions
{
    /// <summary>All switches off (pure syntax parsing, e.g. Trino compliance fixtures).</summary>
    public static SqlTokenSecurityOptions None { get; } = new();

    /// <summary>Strict preset with all token security switches enabled.</summary>
    public static SqlTokenSecurityOptions Strict { get; } = new()
    {
        RejectComments = true,
        RejectBackslashInStrings = true,
        RejectEscapedStringLiterals = true,
        RejectDollarQuoting = true,
        RejectBracketLexerDifferentials = true,
        RejectNonAsciiIdentifiers = true,
        RejectDotsInQuotedIdentifiers = true,
        RejectTimeTravelQueries = true,
        RejectVariableSubstitutionSequences = true
    };

    /// <summary>SQ-02: Reject comments.</summary>
    public bool RejectComments { get; init; }

    /// <summary>SQ-01: Reject backslashes in string literals.</summary>
    public bool RejectBackslashInStrings { get; init; }

    /// <summary>SQ-01: Reject E'...' string type constructors.</summary>
    public bool RejectEscapedStringLiterals { get; init; }

    /// <summary>SQ-02: Reject dollar-quoted strings.</summary>
    public bool RejectDollarQuoting { get; init; }

    /// <summary>
    /// SQL-1: Reject '[' / ']' tokens and string literals / quoted identifiers containing '[', ']', '--' or '/*'.
    /// SQL Server and SQLite lex [...] as a quoted identifier while the Trino grammar lexes it as array syntax, so
    /// string content for the gateway could become executable SQL (and comment out appended filters) on the backend.
    /// </summary>
    public bool RejectBracketLexerDifferentials { get; init; }

    /// <summary>SQ-10: Reject unquoted identifiers containing non-ASCII characters.</summary>
    public bool RejectNonAsciiIdentifiers { get; init; }

    /// <summary>SQ-11: Reject dots inside quoted identifiers.</summary>
    public bool RejectDotsInQuotedIdentifiers { get; init; }

    /// <summary>SQ-13: Reject time-travel syntax (FOR TIMESTAMP/VERSION AS OF).</summary>
    public bool RejectTimeTravelQueries { get; init; }

    /// <summary>
    /// SEC-ADG-10: Reject <c>${</c> in any token. Spark and Databricks may substitute <c>${...}</c> variables in statement text
    /// before parsing, which could change a statement after the gateway checked it.
    /// </summary>
    public bool RejectVariableSubstitutionSequences { get; init; }

    /// <summary>
    /// Derives the token switches from <see cref="RlsOptions"/>. Dollar quoting is always rejected for SQL Server targets.
    /// </summary>
    public static SqlTokenSecurityOptions FromRlsOptions(RlsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SqlTokenSecurityOptions
        {
            RejectComments = options.RejectComments,
            RejectBackslashInStrings = options.RejectBackslashInStrings,
            RejectEscapedStringLiterals = options.RejectEscapedStringLiterals,
            RejectDollarQuoting = options.RejectDollarQuoting || options.TargetDialect == TargetSqlDialect.SqlServer,
            RejectBracketLexerDifferentials = options.RejectBracketLexerDifferentials
                || options.TargetDialect == TargetSqlDialect.SqlServer
                || options.TargetDialect == TargetSqlDialect.Sqlite,
            RejectNonAsciiIdentifiers = options.RejectNonAsciiIdentifiers,
            RejectDotsInQuotedIdentifiers = options.RejectDotsInQuotedIdentifiers,
            RejectTimeTravelQueries = options.RejectTimeTravelQueries,
            RejectVariableSubstitutionSequences = options.TargetDialect == TargetSqlDialect.Databricks
        };
    }
}
