namespace TrinoSqlEngine.Ast.Buffer;

using System;

/// <summary>
/// SEC-ADG-18 / SQLE-10: escapes a gateway value that is used as (part of) a LIKE pattern, so the value never acts as a pattern
/// (%, _, [ and the escape character). Shared by the emitter (cache miss) and the template rebind (cache hit).
/// </summary>
internal static class LikePatternEscaper
{
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new System.Text.StringBuilder(text.Length + 4);
        foreach (char c in text)
        {
            if (c is '\\' or '%' or '_' or '[') sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }
}
