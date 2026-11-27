namespace Autheris.Application.Mcp.Services;

using System;
using System.Text;

/// <summary>
/// The SQL grammar has no <c>@name</c> token: the executor turns a placeholder into a bind slot once the caller supplied it. Table
/// resolution for the governance guardrail only needs the shape of the statement, so every placeholder outside a quoted section is
/// replaced by the anonymous <c>?</c> parameter. The caller's parameters are never read here, a placeholder in an identifier position
/// stays a syntax error, and the text the executor later runs is not affected.
/// </summary>
internal static class McpSqlPlaceholders
{
    internal static string NeutralizeForAnalysis(string sql)
    {
        if (sql.IndexOf('@', StringComparison.Ordinal) < 0)
        {
            return sql;
        }

        var sb = new StringBuilder(sql.Length);
        char quote = '\0';
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote)
                {
                    if (i + 1 < sql.Length && sql[i + 1] == quote)
                    {
                        sb.Append(sql[++i]);
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                quote = c;
                sb.Append(c);
                continue;
            }

            bool startsPlaceholder = c == '@'
                && i + 1 < sql.Length && (char.IsAsciiLetter(sql[i + 1]) || sql[i + 1] == '_')
                && (i == 0 || !(char.IsAsciiLetterOrDigit(sql[i - 1]) || sql[i - 1] is '_' or '@'));
            if (!startsPlaceholder)
            {
                sb.Append(c);
                continue;
            }

            int end = i + 1;
            while (end < sql.Length && (char.IsAsciiLetterOrDigit(sql[end]) || sql[end] == '_'))
            {
                end++;
            }

            sb.Append('?');
            i = end - 1;
        }

        return sb.ToString();
    }
}
