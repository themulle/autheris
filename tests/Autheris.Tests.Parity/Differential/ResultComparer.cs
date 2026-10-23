using System.Globalization;
using System.Text.RegularExpressions;

namespace Autheris.Tests.Parity.Differential;

/// <summary>
/// Canonical comparison of two result sets (D-16): columns matched by name (case-insensitive, any order), NULL-aware, booleans as 1/0,
/// numbers as decimals within an absolute tolerance, a product numeric text equal to a reference number, and (date groupings only) a
/// reference date part (<c>2024</c>, <c>2024-03</c>) equal to the first instant of that period as timestamp text. Rows are compared as a multiset unless <c>ordered</c>.
/// </summary>
public static partial class ResultComparer
{
    public const decimal Tolerance = 0.000001m;

    [GeneratedRegex(@"^\d{4}(-\d{2}(-\d{2})?)?$")]
    private static partial Regex DatePart();

    /// <summary>Midnight, with optional seconds, fraction and zero offset: what follows the date when a bucket start is rendered as a timestamp.</summary>
    [GeneratedRegex(@"^[T ]00:00(:00(\.0+)?)?(Z|[+-]00:?00)?$")]
    private static partial Regex MidnightSuffix();

    /// <summary>Null when the results match, otherwise a one-line description of the first difference.</summary>
    public static string? Compare(ResultSet expected, ResultSet actual, bool ordered, bool dateGrouping = false)
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
            // tolerance-aware multiset match: sorting by a rounded key is unstable at a rounding boundary, so match row by row
            var used = new bool[act.Count];
            for (var r = 0; r < exp.Count; r++)
            {
                var hit = -1;
                for (var a = 0; a < act.Count && hit < 0; a++)
                {
                    if (!used[a] && RowSame(act[a], exp[r], dateGrouping))
                    {
                        hit = a;
                    }
                }

                if (hit < 0)
                {
                    return $"no product row equals reference row {r} [{string.Join(", ", exp[r].Select(Show))}]; closest by order: {Describe(act[r], exp[r], expected.Columns, r, dateGrouping)}";
                }

                used[hit] = true;
            }

            return null;
        }

        for (var r = 0; r < exp.Count; r++)
        {
            if (!RowSame(act[r], exp[r], dateGrouping))
            {
                return Describe(act[r], exp[r], expected.Columns, r, dateGrouping);
            }
        }

        return null;
    }

    private static bool RowSame(object?[] actual, object?[] reference, bool dateGrouping)
    {
        for (var c = 0; c < reference.Length; c++)
        {
            if (!Same(actual[c], reference[c], dateGrouping))
            {
                return false;
            }
        }

        return true;
    }

    private static string Describe(object?[] actual, object?[] reference, IReadOnlyList<string> columns, int row, bool dateGrouping)
    {
        for (var c = 0; c < reference.Length; c++)
        {
            if (!Same(actual[c], reference[c], dateGrouping))
            {
                return $"row {row} column '{columns[c]}': '{Show(actual[c])}' vs reference '{Show(reference[c])}'";
            }
        }

        return $"row {row} differs";
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
        double d when double.IsFinite(d) && Math.Abs(d) < 7.9e27 => (decimal)d,
        float f when float.IsFinite(f) && Math.Abs(f) < 7.9e27f => (decimal)f,
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    private static string Show(object? v) => v is null ? "NULL" : Convert.ToString(v, CultureInfo.InvariantCulture)!;

    private static string Key(object?[] row) => string.Join("\u001f", row.Select(v => v switch
    {
        null => "\u007f",
        decimal d => "n" + Math.Round(d, 6).ToString("0.######", CultureInfo.InvariantCulture),
        _ => "s" + v,
    }));

    private static bool Close(decimal a, decimal e)
    {
        try
        {
            return Math.Abs(a - e) <= Tolerance;
        }
        catch (OverflowException)
        {
            return a == e;
        }
    }

    private static bool Same(object? actual, object? reference, bool dateGrouping)
    {
        if (actual is null || reference is null)
        {
            return actual is null && reference is null;
        }

        if (actual is decimal a && reference is decimal e)
        {
            return Close(a, e);
        }

        // a numeric reference may come back as a numeric text; the opposite direction is a type difference
        if (actual is string asr && reference is decimal rd)
        {
            return decimal.TryParse(asr, NumberStyles.Float, CultureInfo.InvariantCulture, out var an2) && Close(an2, rd);
        }

        if (actual is not string x || reference is not string y)
        {
            return false;
        }

        if (string.Equals(x, y, StringComparison.Ordinal))
        {
            return true;
        }

        return dateGrouping && DatePart().IsMatch(y) && IsPeriodStart(x, y);
    }

    /// <summary>True when <paramref name="x"/> is the first instant of the period named by <paramref name="part"/> (<c>2024-03</c> = <c>2024-03-01T00:00:00Z</c>, never <c>2024-03-15</c>).</summary>
    private static bool IsPeriodStart(string x, string part)
    {
        var full = part.Length switch
        {
            4 => part + "-01-01",
            7 => part + "-01",
            _ => part,
        };
        if (!x.StartsWith(full, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = x[full.Length..];
        return rest.Length == 0 || MidnightSuffix().IsMatch(rest);
    }
}
