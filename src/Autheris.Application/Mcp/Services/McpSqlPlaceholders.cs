namespace Autheris.Application.Mcp.Services;

using Autheris.Application.Sql;

/// <summary>
/// The SQL grammar has no <c>@name</c> token: the executor turns a placeholder into a bind slot once the caller supplied it. Table
/// resolution for the governance guardrail only needs the shape of the statement, so every placeholder outside a quoted section is
/// replaced by the anonymous <c>?</c> parameter, using the same scanner as the executor. The caller's parameters are never read here,
/// a placeholder in an identifier position stays a syntax error, and the text the executor later runs is not affected.
/// </summary>
internal static class McpSqlPlaceholders
{
    internal static string NeutralizeForAnalysis(string sql) =>
        sql.IndexOf('@', System.StringComparison.Ordinal) < 0 ? sql : SqlPlaceholderScanner.Replace(sql, static _ => "?");
}
