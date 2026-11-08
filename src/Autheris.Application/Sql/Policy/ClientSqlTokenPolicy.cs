namespace Autheris.Application.Sql.Policy;

using TrinoSqlEngine;

/// <summary>
/// Token security options for the client SQL path.
/// Accepts comments and a single trailing statement terminator, while keeping all dialect and injection guards active.
/// Emitted SQL is regenerated from the AST and contains no hidden-channel tokens.
/// </summary>
public static class ClientSqlTokenPolicy
{
    public static readonly SqlTokenSecurityOptions Analysis = new()
    {
        RejectComments = false,
        AllowTrailingStatementTerminator = true,
        RejectBackslashInStrings = true,
        RejectEscapedStringLiterals = true,
        RejectNonAsciiIdentifiers = true,
        RejectDotsInQuotedIdentifiers = true,
        RejectTimeTravelQueries = true,
        RejectReservedIdentifiers = true
    };

    public static SqlTokenSecurityOptions Compile(TargetSqlDialect dialect) => new()
    {
        RejectComments = false,
        AllowTrailingStatementTerminator = true,
        RejectBackslashInStrings = true,
        RejectEscapedStringLiterals = true,
        RejectDollarQuoting = true,
        RejectBracketLexerDifferentials = dialect is TargetSqlDialect.SqlServer or TargetSqlDialect.Sqlite,
        RejectNonAsciiIdentifiers = true,
        RejectDotsInQuotedIdentifiers = true,
        RejectTimeTravelQueries = true,
        RejectReservedIdentifiers = true
    };
}
