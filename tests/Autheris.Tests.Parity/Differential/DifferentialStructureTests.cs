using System.Text.RegularExpressions;
using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Seed;
using Shouldly;

namespace Autheris.Tests.Parity.Differential;

/// <summary>Block E structure tests: comparer, allow-list, corpus validity on a private SQLite seed copy, TLP and the negative stub. No host.</summary>
[Trait("Category", "Parity")]
public sealed partial class DifferentialStructureTests : ParityTestBase, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autheris-parity-diff-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteReference _reference;

    public DifferentialStructureTests()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "data.db");
        ParitySeed.Create(path);
        _reference = new SqliteReference(path);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [GeneratedRegex(@"\bWHERE\b.*?(?=\bGROUP BY\b|\bORDER BY\b|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WhereClause();

    private static ResultSet Set(string[] columns, params object?[][] rows) => new(columns, rows);

    [Fact]
    public void Differential_AllowListEntriesHaveReason()
    {
        DifferentialAllowList.Entries.ShouldNotBeEmpty();
        DifferentialAllowList.Entries.ShouldAllBe(e => DifferentialAllowList.HasValidReason(e));
        DifferentialAllowList.HasValidReason(new AllowEntry("x", _ => true, "because")).ShouldBeFalse();
        DifferentialAllowList.HasValidReason(new AllowEntry("x", _ => true, "documented: ")).ShouldBeFalse();
        DifferentialAllowList.HasValidReason(new AllowEntry("x", _ => true, string.Empty)).ShouldBeFalse();
        DifferentialAllowList.HasValidReason(new AllowEntry("x", _ => true, "PARITY-GAP-021")).ShouldBeTrue();
        DifferentialAllowList.HasValidReason(new AllowEntry("x", _ => true, "documented: SQLite coerces")).ShouldBeTrue();
    }

    [Fact]
    public void Differential_ComparerMatchesCanonicalForms()
    {
        var reference = Set(["a", "b"], [1L, 100.5], [2L, null]);
        // column order, numeric type and tolerance, bool as 1
        ResultComparer.Compare(reference, Set(["B", "A"], [100.5000004m, 1], [null, 2]), ordered: true).ShouldBeNull();
        ResultComparer.Compare(Set(["f"], [1L]), Set(["f"], [true]), ordered: true).ShouldBeNull();
        // unordered: row order is irrelevant, ordered: it is not
        ResultComparer.Compare(reference, Set(["a", "b"], [2L, null], [1L, 100.5]), ordered: false).ShouldBeNull();
        ResultComparer.Compare(reference, Set(["a", "b"], [2L, null], [1L, 100.5]), ordered: true).ShouldNotBeNull();
        // a date part matches the start of a timestamp text, a numeric text matches a number
        ResultComparer.Compare(Set(["k"], ["2024-03"]), Set(["k"], ["2024-03-01T00:00:00Z"]), ordered: false).ShouldBeNull();
        ResultComparer.Compare(Set(["k"], ["2024"]), Set(["k"], [2024L]), ordered: false).ShouldBeNull();
    }

    [Fact]
    public void Differential_ComparerDetectsDifferences()
    {
        var reference = Set(["a"], [1L], [2L]);
        ResultComparer.Compare(reference, Set(["a"], [1L]), ordered: false)!.ShouldContain("row count");
        ResultComparer.Compare(reference, Set(["a"], [1L], [3L]), ordered: false).ShouldNotBeNull();
        ResultComparer.Compare(Set(["a"], [1.0]), Set(["a"], [1.01m]), ordered: false).ShouldNotBeNull();
        ResultComparer.Compare(Set(["a"], [0L]), Set(["a"], [new object?[] { null }[0]]), ordered: false).ShouldNotBeNull();
        ResultComparer.Compare(Set(["a"], ["x"]), Set(["a"], ["X"]), ordered: false).ShouldNotBeNull();
        ResultComparer.Compare(Set(["a"], [1L]), Set(["b"], [1L]), ordered: false)!.ShouldContain("missing");
        ResultComparer.Compare(Set(["a"], [1L]), Set(["a", "b"], [1L, 2L]), ordered: false)!.ShouldContain("column count");
        ResultComparer.Compare(Set(["a"]), Set(["a"]), ordered: false).ShouldBeNull();
    }

    [Fact]
    public void Differential_DetectsWrongCompilerStub()
    {
        const string sql = "SELECT id FROM parity_sales WHERE region = 'North' ORDER BY id";
        var honest = _reference.Execute(sql);
        ResultComparer.Compare(honest, _reference.Execute(sql), ordered: true).ShouldBeNull();

        // a compiler that drops the WHERE clause answers a different question
        var dropped = _reference.Execute(WhereClause().Replace(sql, string.Empty));
        dropped.Rows.Count.ShouldBeGreaterThan(honest.Rows.Count);
        ResultComparer.Compare(honest, dropped, ordered: true).ShouldNotBeNull();

        var c = new DifferentialCase("stub", "stub", "WebSql", sql, null, new Matrix.SqlRequest(sql), true);
        DifferentialExecutor.Evaluate(c, honest, ToChannelResult(dropped)).ShouldNotBeNull();
        DifferentialExecutor.Evaluate(c, honest, ToChannelResult(honest)).ShouldBeNull();
        DifferentialExecutor.Evaluate(c, honest, new ChannelResult(400, "SQL_SYNTAX_ERROR", ChannelResult.NoRows, "{}"))!.ShouldContain("HTTP 400");
    }

    [Fact]
    public void Differential_CorpusIsValidSqliteWithUniqueIds()
    {
        var cases = DifferentialCorpus.Cases();
        cases.Count.ShouldBeGreaterThan(50);
        cases.Select(c => $"{c.Channel}|{c.Id}").Distinct().Count().ShouldBe(cases.Count);
        cases.Select(c => c.Channel).Distinct().Order().ShouldBe(["GraphQl", "WebSql"]);
        foreach (var c in cases)
        {
            var reference = Should.NotThrow(() => _reference.Execute(c.ReferenceSql, c.Parameters), c.Id);
            (c.ProductColumns ?? [.. reference.Columns]).Length.ShouldBe(reference.Columns.Count, c.Id);
            (c.GapId is null || KnownGap.IsValidId(c.GapId)).ShouldBeTrue(c.Id);
            (c.GapId is not null && DifferentialAllowList.Find(c) is not null).ShouldBeFalse($"{c.Id}: a gapped case must not be allow-listed");
        }

        DifferentialCorpus.NoReference.Values.ShouldAllBe(r => r.Length > 10);
    }

    [Fact]
    public void Differential_TlpHoldsOnTheReferenceAndDetectsViolations()
    {
        foreach (var (id, predicate) in Tlp.Predicates)
        {
            var q = Tlp.Idq(predicate);
            IEnumerable<long> Ids(string sql) => _reference.Execute(sql).Rows.Select(r => Convert.ToInt64(r[0]));
            Tlp.IdViolation(Ids(q.All), Ids(q.True), Ids(q.False), Ids(q.Null)).ShouldBeNull(id);
            var n = Tlp.Countq(predicate);
            long Count(string sql) => Convert.ToInt64(_reference.Execute(sql).Rows.Single()[0]);
            Tlp.CountViolation(Count(n.All), Count(n.True), Count(n.False), Count(n.Null)).ShouldBeNull(id);
        }

        // the NULL partition is non-empty for some predicates (the three-valued logic is actually exercised)
        var nullPart = Tlp.Predicates.Count(p => _reference.Execute(Tlp.Idq(p.Predicate).Null).Rows.Count > 0);
        nullPart.ShouldBeGreaterThan(3);

        // a compiler that mishandles NOT (drops the NULL rows' partition) breaks the invariant
        Tlp.IdViolation([1, 2, 3], [1], [2], []).ShouldNotBeNull();
        Tlp.IdViolation([1, 2, 3], [1], [2, 2], [3]).ShouldNotBeNull();
        Tlp.IdViolation([1, 2, 3], [1], [2], [3]).ShouldBeNull();
        Tlp.CountViolation(3, 1, 1, 0).ShouldNotBeNull();
        Tlp.CountViolation(3, 1, 1, 1).ShouldBeNull();
    }

    private static ChannelResult ToChannelResult(ResultSet set) => new(
        200,
        null,
        [.. set.Rows.Select(r => (IReadOnlyDictionary<string, object?>)set.Columns.Select((c, i) => (c, v: r[i])).ToDictionary(x => x.c, x => x.v))],
        string.Empty);
}
