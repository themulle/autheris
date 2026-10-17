namespace TrinoSqlEngine.Ast.Security;

using System;

/// <summary>
/// REQ-SQL-130 (A-9): decides whether a catalog column type (or a CAST target) is a plain timestamp, that is a date and time without an offset,
/// for the compile dialect. The table is dialect specific because the same word means different things: <c>timestamp</c> is a
/// <c>rowversion</c> binary type in T-SQL and an offset-less time everywhere else, and <c>AT TIME ZONE</c> flips its meaning for
/// <c>timestamptz</c>. Anything not listed, a null or an unparsable type is not plain (fail closed).
/// </summary>
internal static class PlainTimestampClassifier
{
    public static bool IsPlain(TargetSqlDialect dialect, string? catalogType)
    {
        if (!TryNormalize(catalogType, out string type)) return false;
        return dialect switch
        {
            TargetSqlDialect.SqlServer => type is "datetime" or "datetime2" or "smalldatetime",
            TargetSqlDialect.PostgreSql => type is "timestamp" or "timestamp without time zone",
            TargetSqlDialect.DuckDb => type is "timestamp" or "timestamp_s" or "timestamp_ms" or "timestamp_ns" or "timestamp without time zone",
            _ => false
        };
    }

    /// <summary>A CAST target (<c>timestamp</c>, <c>timestamp(3)</c>, <c>timestamp without time zone</c>) that yields a plain timestamp in every supported dialect.</summary>
    public static bool IsPlainCastTarget(string? targetType) =>
        TryNormalize(targetType, out string type) && type is "timestamp" or "timestamp without time zone";

    /// <summary>Lower case, single spaces, every <c>(digits)</c> precision group removed; false for anything else that contains parentheses.</summary>
    private static bool TryNormalize(string? text, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64) return false;

        var buffer = new System.Text.StringBuilder(text.Length);
        bool pendingSpace = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(')
            {
                int close = text.IndexOf(')', i + 1);
                if (close < 0) return false;
                for (int j = i + 1; j < close; j++)
                {
                    if (!(char.IsAsciiDigit(text[j]) || text[j] == ' ')) return false;
                }

                if (close == i + 1 || text.AsSpan(i + 1, close - i - 1).IsWhiteSpace()) return false;
                i = close;
                continue;
            }

            if (c == ' ')
            {
                pendingSpace = buffer.Length > 0;
                continue;
            }

            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
            if (pendingSpace) buffer.Append(' ');
            pendingSpace = false;
            buffer.Append(char.ToLowerInvariant(c));
        }

        normalized = buffer.ToString();
        return normalized.Length > 0;
    }
}
