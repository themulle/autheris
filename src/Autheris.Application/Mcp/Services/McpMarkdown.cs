namespace Autheris.Application.Mcp.Services;

using System;
using System.Text;

/// <summary>
/// Markdown helpers for MCP resources (MCP-13). Catalog values are externally authored text, so a value placed into a table cell
/// must not be able to end the cell or the row, open a code span or start a new table line.
/// </summary>
public static class McpMarkdown
{
    /// <summary>Escapes <c>|</c>, backslash and backtick and turns CR, LF and other line breaks into spaces. Null becomes <c>-</c>.</summary>
    public static string EscapeCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }

        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '|':
                case '\\':
                case '`':
                    sb.Append('\\').Append(c);
                    break;
                case '\r':
                case '\n':
                case '\u0085':
                case '\u2028':
                case '\u2029':
                    sb.Append(' ');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }
}
