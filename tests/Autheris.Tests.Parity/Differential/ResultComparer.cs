using System.Globalization;
using System.Text.RegularExpressions;

namespace Autheris.Tests.Parity.Differential;

/// <summary>
/// Canonical comparison of two result sets (D-16): columns matched by name (case-insensitive, any order), NULL-aware, booleans as 1/0,
/// numbers as decimals within an absolute tolerance, a numeric text equal to a number, a date part (<c>2024</c>, <c>2024-03</c>) equal to
/// the start of a timestamp text. Rows are compared as a multiset unless <c>ordered</c>.
/// </summary>
public static partial class ResultComparer
{
    public const decimal Tolerance = 0.000001m;

    [GeneratedRegex(@"^\d{4}(-\d{2}(-\d{2})?)?$")]
    private static partial Regex DatePart();

    /// <summary>Null when the results match, otherwise a one-line description of the first difference.</summary>
    public static string? Compare(ResultSet expected, ResultSet actual, bool ordered)
    {
        var map = new int[expected.Columns.Count];
        for (var i = 0; i < map.Length; i++)
        {
            map[i] = IndexOf(actual.Columns, expected.Columns[i]);
            if (map[i] < 0)
            {
                return $"column '{expected.Columns[i]}' missing in [{string.Join(",", actual.Columns)}]";
            }
        }

        if (actual.Columns.Count != expected.Columns.Count)
        {
            return $"column count {actual.Columns.Count} vs {expected.Columns.Count}: [{string.Join(",", actual.Columns)}] vs [{string.Join(",", expected.Columns)}]";
        }

        var exp = expected.Rows.Select(r => r.Select(Canonical).ToArray()).ToList();
        var act = actual.Rows.Select(r => map.Select(i => Canonical(r[i])).ToArray()).ToList();
        if (exp.Count != act.Count)
        {
            return $"row count {act.Count} vs reference {exp.Count}";
        }

        if (!ordered)
        {
            exp = exp.OrderBy(Key, StringComparer.Ordinal).ToList();
            act = act.OrderBy(Key, StringComparer.Ordinal).ToList();
        }

        for (var r = 0; r < exp.Count; r++)
        {
            for (var c = 0; c < map.Length; c++)
            {
                if (!Same(act[r][c], exp[r][c]))
                {
                    return $"row {r} column '{expected.Columns[c]}': '{Show(act[r][c])}' vs reference '{Show(exp[r][c])}'";
                }
            }
        }

        return null;
    }

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Null, decimal or string.</summary>
    private static object? Canonical(object? v) => v switch
    {
        null => null,
        bool b => b ? 1m : 0m,
        int or long or short or byte => Convert.ToDecimal(v, CultureInfo.InvariantCulture),
        decimal d => d,
        double d when double.IsFinite(d) => (decimal)d,
        float f when float.IsFinite(f) => (decimal)f,
        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    private static string Show(object? v) => v is null ? "NULL" : Convert.ToString(v, CultureInfo.InvariantCulture)!;

    private static string Key(object?[] row) => string.Join("\u001f", row.Select(v => v switch
    {
        null => "\u007f",
        decimal d => "n" + Math.Round(d, 6).ToString("0.######", CultureInfo.InvariantCulture),
        _ => "s" + v,
    }));

    private static bool Same(object? actual, object? reference)
    {
        if (actual is null || reference is null)
        {
            return actual is null && reference is null;
        }

        if (actual is decimal a && reference is decimal e)
        {
            return Math.Abs(a - e) <= Tolerance;
        }

        if (actual is decimal an && reference is string rs)
        {
            return decimal.TryParse(rs, NumberStyles.Float, CultureInfo.InvariantCulture, out var rn) && Math.Abs(an - rn) <= Tolerance;
        }

        if (actual is string asr && reference is decimal rd)
        {
            return decimal.TryParse(asr, NumberStyles.Float, CultureInfo.InvariantCulture, out var an2) && Math.Abs(an2 - rd) <= Tolerance;
        }

        var (x, y) = ((string)actual, (string)reference);
        if (DatePart().IsMatch(y))
        {
            return x.StartsWith(y, StringComparison.Ordinal);
        }

        return string.Equals(x, y, StringComparison.Ordinal);
    }
}
