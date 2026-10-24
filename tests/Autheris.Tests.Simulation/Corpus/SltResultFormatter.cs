using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Autheris.Tests.Simulation.Corpus;

/// <summary>
/// The sqllogictest result format: <c>I</c> integer, <c>R</c> <c>%.3f</c>, <c>T</c> text (<c>(empty)</c> for the empty string, control and non-ASCII
/// characters shown as <c>@</c>), <c>NULL</c> for null; then the sort mode; the md5 hash is taken over every value followed by a newline.
/// </summary>
public static class SltResultFormatter
{
    public static IReadOnlyList<string> Format(IReadOnlyList<object?[]> rows, string types, SltSortMode sort)
    {
        var formatted = new List<string[]>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Length != types.Length)
            {
                throw new InvalidOperationException($"result has {row.Length} columns, the record declares {types.Length}.");
            }

            var cells = new string[row.Length];
            for (var c = 0; c < row.Length; c++)
            {
                cells[c] = Cell(row[c], types[c]);
            }

            formatted.Add(cells);
        }

        switch (sort)
        {
            case SltSortMode.RowSort:
                formatted.Sort(CompareRows);
                break;
            case SltSortMode.ValueSort:
                var flat = formatted.SelectMany(r => r).ToList();
                flat.Sort(string.CompareOrdinal);
                return flat;
        }

        return [.. formatted.SelectMany(r => r)];
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "The sqllogictest format records md5 digests of result values; it is a checksum of test data, not a security control.")]
    public static string Md5(IEnumerable<string> values)
    {
        var sb = new StringBuilder();
        foreach (var v in values)
        {
            sb.Append(v).Append('\n');
        }

        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>True when the formatted values equal the recorded expectation (values or hash).</summary>
    public static bool Matches(IReadOnlyList<string> values, SltExpected expected) => expected switch
    {
        SltValues v => values.SequenceEqual(v.Values, StringComparer.Ordinal),
        SltHash h => values.Count == h.Count && string.Equals(Md5(values), h.Md5, StringComparison.Ordinal),
        _ => false,
    };

    private static int CompareRows(string[] a, string[] b)
    {
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var c = string.CompareOrdinal(a[i], b[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    private static string Cell(object? value, char type)
    {
        if (value is null or DBNull)
        {
            return "NULL";
        }

        return type switch
        {
            'I' => value switch
            {
                long l => l.ToString(CultureInfo.InvariantCulture),
                int n => n.ToString(CultureInfo.InvariantCulture),
                double d => ((long)d).ToString(CultureInfo.InvariantCulture),
                decimal m => ((long)m).ToString(CultureInfo.InvariantCulture),
                string s => LeadingInteger(s).ToString(CultureInfo.InvariantCulture),
                _ => "0",
            },
            'R' => value switch
            {
                double d => d.ToString("F3", CultureInfo.InvariantCulture),
                long l => ((double)l).ToString("F3", CultureInfo.InvariantCulture),
                int n => ((double)n).ToString("F3", CultureInfo.InvariantCulture),
                decimal m => m.ToString("F3", CultureInfo.InvariantCulture),
                string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d.ToString("F3", CultureInfo.InvariantCulture),
                _ => "0.000",
            },
            _ => Text(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
        };
    }

    private static string Text(string s)
    {
        if (s.Length == 0)
        {
            return "(empty)";
        }

        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] < 32 || chars[i] > 126)
            {
                chars[i] = '@';
            }
        }

        return new string(chars);
    }

    private static long LeadingInteger(string s)
    {
        var t = s.TrimStart();
        var end = 0;
        if (end < t.Length && (t[end] is '-' or '+'))
        {
            end++;
        }

        while (end < t.Length && char.IsAsciiDigit(t[end]))
        {
            end++;
        }

        return long.TryParse(t.AsSpan(0, end), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}

/// <summary>REQ-SIM-12: seeded sample. Top-<c>size</c> by <c>SimSeed.Hash(seed, id)</c>, executed in (file, line) order.</summary>
public static class SltSampler
{
    public const string EnvVar = "AUTHERIS_SIM_SAMPLE";
    public const int DefaultSize = 5000;

    /// <summary>Parses <c>^[0-9]{1,5}$|^all$</c>; <c>all</c> is <see cref="int.MaxValue"/>. Empty means <see cref="DefaultSize"/>. Anything else fails closed.</summary>
    public static int ResolveSize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return DefaultSize;
        }

        if (value == "all")
        {
            return int.MaxValue;
        }

        if (value.Length is >= 1 and <= 5 && value.All(char.IsAsciiDigit))
        {
            return int.Parse(value, CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException($"{EnvVar} must match ^[0-9]{{1,5}}$|^all$.");
    }

    public static IReadOnlyList<SltQuery> Select(IReadOnlyList<SltQuery> queries, long seed, int size) =>
    [
        .. queries
            .OrderBy(q => Infrastructure.SimSeed.Hash(seed, q.Id))
            .ThenBy(q => q.Id, StringComparer.Ordinal)
            .Take(size)
            .OrderBy(q => q.File, StringComparer.Ordinal)
            .ThenBy(q => q.Line),
    ];
}
