namespace Autheris.Tests.Simulation.Corpus;

/// <summary>
/// Scenario B probes on our own table: <c>t1</c> holds <c>a = 100..139</c> (and <c>b = a + 1</c>), the row filter is <c>a &gt; 120</c>, so exactly 19 rows
/// (<c>121..139</c>) may ever be visible. Subqueries, joins, set operations, CTEs and aggregates must all see the filtered table.
/// A probe that the compiler rejects is not a leak; a crash, a hang or a row outside the filter is.
/// </summary>
public static class SltRowFilterProbes
{
    private sealed record Probe(string Sql, long? Scalar);

    private static readonly Probe[] Probes =
    [
        new("SELECT a FROM t1", null),
        new("SELECT a FROM t1 ORDER BY a LIMIT 5", null),
        new("SELECT count(*) FROM t1", 19),
        new("SELECT count(*) FROM t1 WHERE a <= 120", 0),
        new("SELECT min(a) FROM t1", 121),
        new("SELECT a FROM t1 WHERE a IN (SELECT a FROM t1)", null),
        new("SELECT x.a FROM t1 x, t1 y WHERE x.a = y.a", null),
        new("SELECT a FROM t1 WHERE a < 100 OR 1 = 1", null),
        new("SELECT a FROM t1 UNION ALL SELECT a FROM t1", null),
        new("SELECT a FROM (SELECT a FROM t1) s", null),
        new("WITH c AS (SELECT a FROM t1) SELECT a FROM c", null),
        new("SELECT count(*) FROM t1 WHERE EXISTS (SELECT 1 FROM t1 s WHERE s.a = 105)", 0),
        new("SELECT t1.a FROM t1 LEFT JOIN t1 u ON u.a = t1.a - 20", null),
        new("SELECT b - 1 FROM t1", null),
    ];

    public static int ProbeCount => Probes.Length;

    /// <summary>Returns the violations (empty when the filter holds) and the number of probes that produced rows.</summary>
    public static async Task<(IReadOnlyList<string> Violations, int Answered)> RunAsync()
    {
        await using var reference = SltReference.Create();
        await reference.ExecuteAsync("CREATE TABLE t1(a INTEGER, b INTEGER, c INTEGER, d INTEGER, e INTEGER)");
        for (var a = 100; a < 140; a++)
        {
            await reference.ExecuteAsync($"INSERT INTO t1 VALUES({a}, {a + 1}, {a + 2}, {a + 3}, {a + 4})");
        }

        using var connection = SltReference.OpenConnection(reference.Path, readOnly: true);
        var runner = new SltEngineRunner(SltScenario.RowFilter, connection);
        var violations = new List<string>();
        var answered = 0;
        foreach (var probe in Probes)
        {
            switch (runner.Run(probe.Sql))
            {
                case SltEngineRows rows:
                    answered++;
                    if (probe.Scalar is { } expected)
                    {
                        if (rows.Rows.Count != 1 || rows.Rows[0][0] is not long actual || actual != expected)
                        {
                            violations.Add($"scalar probe differs: {probe.Sql}");
                        }
                    }
                    else if (rows.Rows.Any(r => r[0] is not long v || v <= SltEngineRunner.FilterMinExclusive))
                    {
                        violations.Add($"row outside the filter returned: {probe.Sql}");
                    }

                    break;
                case SltEngineRejected:
                    break;
                default:
                    violations.Add($"crash or hang: {probe.Sql}");
                    break;
            }
        }

        return (violations, answered);
    }
}
