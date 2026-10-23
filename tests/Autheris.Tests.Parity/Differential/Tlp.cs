using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Matrix;
using Autheris.Tests.Parity.Seed;
using Xunit.Sdk;

namespace Autheris.Tests.Parity.Differential;

/// <summary>
/// Ternary logic partitioning (TLP) over the filter shapes: <c>Q == Q WHERE p UNION ALL Q WHERE NOT p UNION ALL Q WHERE p IS NULL</c>,
/// checked for the id list and for COUNT(*), through WebSQL and on the SQLite reference (the reference must satisfy the invariant itself).
/// </summary>
public static class Tlp
{
    private const string Table = ParitySeed.SalesTable;

    /// <summary>Filter shapes of the matrix and the pairwise generator, plus NULL-sensitive compound predicates.</summary>
    public static readonly IReadOnlyList<(string Id, string Predicate)> Predicates =
    [
        ("eq", "region = 'North'"),
        ("neq", "region <> 'North'"),
        ("compare_numeric", "qty > 6"),
        ("range", "amount >= 100 AND amount < 300"),
        ("range_date", "sale_date >= '2024-02-29' AND sale_date < '2024-03-31'"),
        ("like", "note LIKE '%off%'"),
        ("in_list", "region IN ('North', 'Empty')"),
        ("is_null", "region IS NULL"),
        ("is_not_null", "note IS NOT NULL"),
        ("bool_eq", "is_return = 1"),
        ("or_null", "product = 'Widget' OR qty IS NULL"),
        ("and_null", "region = 'South' AND note IS NULL"),
        ("not_in", "region NOT IN ('North', 'South')"),
        ("between", "qty BETWEEN 3 AND 7"),
        ("col_vs_col", "qty > region_id"),
        ("amount_eq", "amount = 200"),
    ];

    public static (string All, string True, string False, string Null) Idq(string predicate) => (
        $"SELECT id FROM {Table}",
        $"SELECT id FROM {Table} WHERE {predicate}",
        $"SELECT id FROM {Table} WHERE NOT ({predicate})",
        $"SELECT id FROM {Table} WHERE ({predicate}) IS NULL");

    public static (string All, string True, string False, string Null) Countq(string predicate) => (
        $"SELECT COUNT(*) AS n FROM {Table}",
        $"SELECT COUNT(*) AS n FROM {Table} WHERE {predicate}",
        $"SELECT COUNT(*) AS n FROM {Table} WHERE NOT ({predicate})",
        $"SELECT COUNT(*) AS n FROM {Table} WHERE ({predicate}) IS NULL");

    /// <summary>Null when the three partitions together equal the unpartitioned id multiset.</summary>
    public static string? IdViolation(IEnumerable<long> all, IEnumerable<long> whenTrue, IEnumerable<long> whenFalse, IEnumerable<long> whenNull)
    {
        var expected = all.Order().ToList();
        var actual = whenTrue.Concat(whenFalse).Concat(whenNull).Order().ToList();
        return expected.SequenceEqual(actual)
            ? null
            : $"partitions hold {actual.Count} ids [{string.Join(",", actual)}], the unpartitioned query {expected.Count} ids [{string.Join(",", expected)}]";
    }

    public static string? CountViolation(long all, long whenTrue, long whenFalse, long whenNull) =>
        all == whenTrue + whenFalse + whenNull ? null : $"COUNT(*) {all} != {whenTrue} + {whenFalse} + {whenNull}";

    public static async Task RunProductAsync(IParityHost host, SqliteReference reference, string id)
    {
        var predicate = Predicates.Single(p => p.Id == id).Predicate;
        var client = new MatrixExecutor(host);

        async Task<List<long>> ProductIds(string sql) => ToIds(await client.ExecuteAsync(new SqlRequest(sql)));
        async Task<long> ProductCount(string sql) => ToIds(await client.ExecuteAsync(new SqlRequest(sql))).Single();

        var ids = Idq(predicate);
        var refViolation = IdViolation(RefIds(reference, ids.All), RefIds(reference, ids.True), RefIds(reference, ids.False), RefIds(reference, ids.Null));
        if (refViolation is not null)
        {
            throw new ParityHardFailureException($"tlp.{id}: the SQLite reference violates the invariant: {refViolation}");
        }

        var violation = IdViolation(await ProductIds(ids.All), await ProductIds(ids.True), await ProductIds(ids.False), await ProductIds(ids.Null));
        if (violation is not null)
        {
            throw new XunitException($"tlp.{id} ids: {violation}; predicate: {predicate}");
        }

        var counts = Countq(predicate);
        violation = CountViolation(await ProductCount(counts.All), await ProductCount(counts.True), await ProductCount(counts.False), await ProductCount(counts.Null));
        if (violation is not null)
        {
            throw new XunitException($"tlp.{id} count: {violation}; predicate: {predicate}");
        }
    }

    private static List<long> RefIds(SqliteReference reference, string sql) =>
        [.. reference.Execute(sql).Rows.Select(r => Convert.ToInt64(r[0], System.Globalization.CultureInfo.InvariantCulture))];

    /// <summary>The first column of every row as a number (a non-2xx answer is a failed TLP cell).</summary>
    private static List<long> ToIds(Channels.ChannelResult result)
    {
        if (result.Status is < 200 or >= 300)
        {
            throw new XunitException($"HTTP {result.Status} code '{result.Code}': {Robin.RobinAssert.Snippet(result.RawBody)}");
        }

        return [.. result.Rows.Select(r => Convert.ToInt64(r.Values.First(), System.Globalization.CultureInfo.InvariantCulture))];
    }
}
