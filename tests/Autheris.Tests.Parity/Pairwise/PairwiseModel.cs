using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Matrix;
using Autheris.Tests.Parity.Seed;

namespace Autheris.Tests.Parity.Pairwise;

/// <summary>One generated case (D-14). The id is the readable factor list, unique and stable for a seed.</summary>
public sealed record PairwiseCase(string Aggregate, string Grouping, string Filter, string Type, string Dialect)
{
    public string Id => $"{Aggregate}_{Grouping}_{Filter}_{Type}_{Dialect}";
}

public static class PairwiseModel
{
    public const string Sqlite = "sqlite";
    public const string Postgres = "postgres";
    public const string SqlServer = "sqlserver";

    public static readonly IReadOnlyList<string> Aggregates = ["count", "count_distinct", "sum", "avg", "min", "max"];
    public static readonly IReadOnlyList<string> Groupings = ["none", "column", "year", "month", "day", "expression"];
    public static readonly IReadOnlyList<string> Filters = ["none", "empty", "eq", "range", "like", "is_null", "in_list", "in_empty", "param"];
    public static readonly IReadOnlyList<string> Types = ["int", "decimal", "string", "date", "timestamp", "bool"];
    public static readonly IReadOnlyList<string> Dialects = [Sqlite, Postgres, SqlServer];

    /// <summary>The four dialect-independent factors; the case list of each dialect is generated over these (see <see cref="Cases"/>).</summary>
    public static readonly IReadOnlyList<Factor> BaseFactors =
    [
        new("aggregate", Aggregates),
        new("grouping", Groupings),
        new("filter", Filters),
        new("type", Types),
    ];

    public static readonly IReadOnlyList<Factor> AllFactors = [.. BaseFactors, new("dialect", Dialects)];

    /// <summary>
    /// Pairwise cases over aggregate x grouping x filter x type, replicated for every dialect: each dialect receives the full pairwise set
    /// (so a SQLite-only run still covers all pairs), and every pair that involves the dialect factor is covered by construction.
    /// </summary>
    public static IReadOnlyList<PairwiseCase> Cases(int seed)
    {
        var baseTests = PairwiseGenerator.Generate(BaseFactors, seed);
        return Dialects.SelectMany(d => baseTests.Select(t => new PairwiseCase(Aggregates[t[0]], Groupings[t[1]], Filters[t[2]], Types[t[3]], d))).ToList();
    }

    public static IReadOnlyList<PairwiseCase> Cases() => Cases(PairwiseGenerator.SeedFrom(ParityEnvironment.Seed));

    /// <summary>Case tests of the cases as value-index rows over <see cref="AllFactors"/> (for the pair-coverage test).</summary>
    public static IReadOnlyList<int[]> AsIndexRows(IEnumerable<PairwiseCase> cases) =>
        cases.Select(c => new[] { Aggregates.ToList().IndexOf(c.Aggregate), Groupings.ToList().IndexOf(c.Grouping), Filters.ToList().IndexOf(c.Filter), Types.ToList().IndexOf(c.Type), Dialects.ToList().IndexOf(c.Dialect) }).ToList();

    public static string Column(string type) => type switch
    {
        "int" => "qty",
        "decimal" => "amount",
        "string" => "product",
        "date" => "sale_date",
        "timestamp" => "sold_at",
        "bool" => "is_return",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static ParityFn Fn(string aggregate) => aggregate switch
    {
        "count" => ParityFn.Count,
        "count_distinct" => ParityFn.CountDistinct,
        "sum" => ParityFn.Sum,
        "avg" => ParityFn.Avg,
        "min" => ParityFn.Min,
        "max" => ParityFn.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(aggregate), aggregate, null),
    };

    /// <summary>Constraint table: aggregate/type combinations that the product must reject with a stable 4xx (D-14).</summary>
    public static bool IsInvalidCombination(string aggregate, string type) =>
        (aggregate is "sum" or "avg" && type is not ("int" or "decimal")) || (aggregate is "min" or "max" && type == "bool");

    public static Func<SaleRow, bool> Predicate(string filter) => filter switch
    {
        "none" or "empty" => static _ => true,
        "eq" => static r => r.Region == "North",
        "range" => static r => r.Qty is >= 3 and <= 7,
        "like" => static r => r.Note?.Contains("off", StringComparison.Ordinal) == true,
        "is_null" => static r => r.Region is null,
        "in_list" => static r => r.Product is "Widget" or "Gizmo",
        "in_empty" => static _ => false,
        "param" => static r => r.Region == "South",
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, null),
    };

    public static Func<SaleRow, object?>? GroupKey(string grouping) => grouping switch
    {
        "none" => null,
        "column" => static r => r.Region,
        "year" => ParityOracle.Year,
        "month" => ParityOracle.Month,
        "day" => ParityOracle.Day,
        "expression" => static r => r.Region?.ToLowerInvariant(),
        _ => throw new ArgumentOutOfRangeException(nameof(grouping), grouping, null),
    };
}
