using TrinoSqlEngine.Ast.Nodes;
using Xunit;

namespace TrinoSqlEngine.Tests.Conformance;

/// <summary>
/// G2 conformance matrix (plan 9.3, WP-X0t): every fixture is a Trino input with the expected SQL text, parameter list and outcome per
/// production dialect. The expected values are golden files under <c>Fixtures/</c>; a deliberate change of generator output is
/// accepted by regenerating them (<c>CONFORMANCE_UPDATE=1</c>) and reviewing the diff.
/// </summary>
[Trait("Category", "SqlGate")]
public class ConformanceMatrixTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var (name, _) in ConformanceDialects.All)
        {
            foreach (var c in ConformanceStore.Load(name).Cases)
            {
                yield return new object[] { name, c.Id };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Fixture_MatchesGeneratorOutput(string dialectName, string id)
    {
        var dialect = ConformanceDialects.Parse(dialectName);

        // A dialect without a capability entry cannot generate yet (SQLite until WP-X0h merges); its golden file is already checked in
        // and every case is enforced as soon as the entry exists. ConformanceCoverageTests pins that the exemption cannot outlive it.
        if (ConformanceVerifier.IsPending(dialect)) return;

        var fixture = ConformanceStore.Load(dialectName).Cases.Single(c => c.Id == id);
        var observed = ConformanceRunner.Run(fixture.Sql, fixture.Group, dialect);

        Assert.Null(ConformanceVerifier.Mismatch(fixture, observed));
    }
}
