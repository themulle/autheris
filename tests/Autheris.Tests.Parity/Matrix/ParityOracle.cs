using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Seed;

namespace Autheris.Tests.Parity.Matrix;

/// <summary>
/// Expected values computed in C# over the <see cref="ParitySeed"/> row literals (never by querying the product): NULL-aware aggregates,
/// grouping keys and filter predicates. The matrix (block A) and the pairwise cases (block B) share it.
/// </summary>
public static class ParityOracle
{
    public static IReadOnlyList<SaleRow> Rows => ParitySeed.Sales;

    /// <summary>Column value as a comparable literal (bool as 1/0, decimal as decimal, dates and timestamps as their ISO text).</summary>
    public static object? Column(SaleRow r, string column) => column switch
    {
        "id" => r.Id,
        "region_id" => r.RegionId,
        "region" => r.Region,
        "product" => r.Product,
        "qty" => r.Qty,
        "amount" => r.Amount,
        "sale_date" => r.SaleDate,
        "sold_at" => r.SoldAt,
        "is_return" => r.IsReturn is { } b ? (b ? 1 : 0) : null,
        "note" => r.Note,
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Unknown seed column."),
    };

    /// <summary>SQL aggregate semantics: COUNT(*) counts rows, other functions ignore NULLs; SUM/AVG/MIN/MAX of nothing is NULL.</summary>
    public static object? Aggregate(IEnumerable<SaleRow> rows, ParityFn fn, string? column)
    {
        var all = rows.ToList();
        if (fn == ParityFn.Count && column is null)
        {
            return all.Count;
        }

        var values = all.Select(r => Column(r, column!)).Where(v => v is not null).Select(v => v!).ToList();
        return fn switch
        {
            ParityFn.Count => values.Count,
            ParityFn.CountDistinct => values.Distinct().Count(),
            ParityFn.Sum => values.Count == 0 ? null : values.Sum(ToDecimal),
            ParityFn.Avg => values.Count == 0 ? null : values.Average(ToDecimal),
            ParityFn.Min => values.Count == 0 ? null : values.Min(Comparable.Instance),
            ParityFn.Max => values.Count == 0 ? null : values.Max(Comparable.Instance),
            _ => throw new ArgumentOutOfRangeException(nameof(fn)),
        };
    }

    public static decimal ToDecimal(object v) => v switch
    {
        int i => i,
        decimal d => d,
        _ => throw new InvalidOperationException($"Not numeric: {v.GetType().Name}"),
    };

    /// <summary>Groups rows by a key function (NULL keys form their own group) and returns <c>[key, aggregates...]</c> rows.</summary>
    public static object?[][] Grouped(IEnumerable<SaleRow> rows, Func<SaleRow, object?> key, params (ParityFn Fn, string? Column)[] aggregates) =>
        rows.GroupBy(key).Select(g => new[] { g.Key }.Concat(aggregates.Select(a => Aggregate(g, a.Fn, a.Column))).ToArray()).ToArray();

    public static object? Year(SaleRow r) => r.SaleDate[..4];

    public static object? Month(SaleRow r) => r.SaleDate[..7];

    public static object? Day(SaleRow r) => r.SaleDate;

    public static object?[][] Ids(IEnumerable<SaleRow> rows) => rows.OrderBy(r => r.Id).Select(r => new object?[] { r.Id }).ToArray();

    private sealed class Comparable : IComparer<object>
    {
        public static readonly Comparable Instance = new();

        public int Compare(object? x, object? y) => (x, y) switch
        {
            (string a, string b) => string.CompareOrdinal(a, b),
            (int a, int b) => a.CompareTo(b),
            _ => ToDecimal(x!).CompareTo(ToDecimal(y!)),
        };
    }
}
