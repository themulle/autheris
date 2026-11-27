namespace Autheris.Application.Sql;

using System;
using System.Text;

/// <summary>
/// The one scanner for <c>@name</c> placeholders in client SQL text, shared by the executor (which turns a supplied placeholder into a
/// bind slot) and the MCP guardrail (which only needs the statement shape for table resolution). A placeholder starts at an
/// <c>@</c> followed by an ASCII letter or underscore, is not preceded by an ASCII letter, digit, underscore or <c>@</c>, and lies
/// outside a single, double or backtick quoted section (a doubled quote character stays inside the section).
/// </summary>
internal static class SqlPlaceholderScanner
{
    /// <summary>
    /// Returns <paramref name="sql"/> with every placeholder whose name <paramref name="map"/> answers replaced by the answer.
    /// A <c>null</c> answer leaves the placeholder untouched; the scan then continues right after the <c>@</c>.
    /// </summary>
    internal static string Replace(string sql, Func<string, string?> map)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(map);

        var sb = new StringBuilder(sql.Length + 16);
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
            if (startsPlaceholder)
            {
                int end = i + 1;
                while (end < sql.Length && (char.IsAsciiLetterOrDigit(sql[end]) || sql[end] == '_'))
                {
                    end++;
                }

                string? replacement = map(sql[(i + 1)..end]);
                if (replacement is not null)
                {
                    sb.Append(replacement);
                    i = end - 1;
                    continue;
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
