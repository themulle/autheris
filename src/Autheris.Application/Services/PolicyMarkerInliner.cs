namespace Autheris.Application.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security;
using System.Text;

/// <summary>
/// Legacy consumers that cannot bind parameters (the in-memory row filter, and the dataset query whose text is parsed as user SQL by the
/// governed pipeline) read the marker text of a typed row policy through this class: each policy marker (<c>@__gql_rls_N</c>,
/// <c>@__gql_vf_N_K</c>, <c>@__pl_hash_N</c>) outside a quoted section is replaced by the literal of its bound value. This is the only place
/// where a policy value becomes text; it is removed together with those two consumers (WP-X0n, X1). Fail closed: an unbound marker or a value
/// that has no safe literal form throws, and the consumer denies.
/// </summary>
internal static class PolicyMarkerInliner
{
    public static string Inline(string sql, IReadOnlyDictionary<string, object?>? parameters)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (sql.IndexOf('@', StringComparison.Ordinal) < 0)
        {
            return sql;
        }

        Dictionary<string, object?>? values = null;
        var result = new StringBuilder(sql.Length + 16);
        char quote = '\0';
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (quote != '\0')
            {
                result.Append(c);
                if (c == quote)
                {
                    if (i + 1 < sql.Length && sql[i + 1] == quote)
                    {
                        result.Append(sql[++i]);
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                result.Append(c);
                continue;
            }

            if (c == '@' && IsMarkerStart(sql, i + 1))
            {
                int end = i + 1;
                while (end < sql.Length && (char.IsAsciiLetterOrDigit(sql[end]) || sql[end] == '_'))
                {
                    end++;
                }

                values ??= Normalize(parameters);
                string name = sql[(i + 1)..end];
                if (!values.TryGetValue(name, out var value))
                {
                    throw new SecurityException("A row policy marker has no bound value (fail-closed).");
                }

                result.Append(Literal(value));
                i = end - 1;
                continue;
            }

            result.Append(c);
        }

        return result.ToString();
    }

    private static bool IsMarkerStart(string sql, int index) =>
        string.CompareOrdinal(sql, index, "__gql_", 0, 6) == 0 || string.CompareOrdinal(sql, index, "__pl_", 0, 5) == 0;

    private static Dictionary<string, object?> Normalize(IReadOnlyDictionary<string, object?>? parameters)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (parameters != null)
        {
            foreach (var (name, value) in parameters)
            {
                map[name.TrimStart('@', ':')] = value;
            }
        }

        return map;
    }

    private static string Literal(object? value) => value switch
    {
        string s when s.Contains('\0') => throw new SecurityException("A row policy value has no safe literal form (fail-closed)."),
        string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
        bool b => b ? "true" : "false",
        long or int or short or byte or sbyte or ushort or uint or ulong => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double d when double.IsFinite(d) => d.ToString("R", CultureInfo.InvariantCulture),
        float f when float.IsFinite(f) => f.ToString("R", CultureInfo.InvariantCulture),
        _ => throw new SecurityException("A row policy value has no safe literal form (fail-closed).")
    };
}
