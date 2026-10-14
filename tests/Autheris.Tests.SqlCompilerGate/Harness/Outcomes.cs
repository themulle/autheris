namespace Autheris.Tests.SqlCompilerGate.Harness;

using System.Globalization;
using System.Text;

public enum RejectionStage { Compile, Execute }

/// <summary>The result of one engine on one statement, scenario and database.</summary>
public abstract record EngineOutcome
{
    /// <summary>The engine refused the statement (a compile-time rejection or a database error).</summary>
    /// <param name="Reason">The exception type name only. Never a message: messages may carry values.</param>
    /// <param name="Governed">The rejection is a typed, expected governance outcome (not an unexpected exception type).</param>
    public sealed record Rejected(RejectionStage Stage, string Reason, bool Governed) : EngineOutcome;

    /// <summary>A query result: the typed cells of every row.</summary>
    public sealed record Rows(IReadOnlyList<object?[]> Data) : EngineOutcome;

    /// <summary>A write: the changed rows (before images with sign <c>-</c>, after images with sign <c>+</c>) over the whole fixture.</summary>
    public sealed record Written(IReadOnlyList<RowChange> Changes) : EngineOutcome;
}

/// <summary>One row image of a write, for the oracle: its table and the typed cells.</summary>
public sealed record RowChange(char Sign, string Table, object?[] Cells);

/// <summary>Typed normalization of cells (plan 16.5 M-1): numeric scale and timestamp precision. Nothing else is normalized.</summary>
public static class CellNormalizer
{
    public static string Raw(object? value) => value switch
    {
        null or DBNull => "null",
        string s => "s:" + s,
        byte[] b => "x:" + Convert.ToHexString(b),
        DateTime t => "t:" + t.ToString("O", CultureInfo.InvariantCulture),
        decimal d => "d:" + d.ToString(CultureInfo.InvariantCulture),
        _ => value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    public static string Normalize(object? value)
    {
        switch (value)
        {
            case null or DBNull:
                return "null";
            case string s:
                return "s:" + s;
            case byte[] b:
                return "x:" + Convert.ToHexString(b);
            case bool flag:
                return "b:" + (flag ? "1" : "0");
            case DateTime t:
                return "t:" + t.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
            case DateTimeOffset o:
                return "t:" + o.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return "n:" + Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("0.#############################", CultureInfo.InvariantCulture);
            case decimal d:
                return "n:" + d.ToString("0.#############################", CultureInfo.InvariantCulture);
            case float or double:
                return "n:" + Math.Round(Convert.ToDecimal(value, CultureInfo.InvariantCulture), 10).ToString("0.#############################", CultureInfo.InvariantCulture);
            default:
                return value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }

    public static string RowKey(IEnumerable<object?> cells, Func<object?, string> cell)
    {
        var sb = new StringBuilder();
        foreach (var c in cells)
        {
            sb.Append(cell(c).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal)).Append('|');
        }

        return sb.ToString();
    }

    public static Dictionary<string, int> Multiset(IEnumerable<string> keys)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var k in keys) map[k] = map.GetValueOrDefault(k) + 1;
        return map;
    }
}
