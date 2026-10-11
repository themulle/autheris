using System.Text.Json;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

namespace TrinoSqlEngine.Tests.Conformance;

/// <summary>
/// G2 structure checks (plan 9.3): at least 300 cases per production dialect, a golden entry for every corpus input in every dialect,
/// and 100% node coverage: <c>EveryNode_HasFixtureOrRejection_PerDialect</c>.
/// </summary>
[Trait("Category", "SqlGate")]
public class ConformanceCoverageTests
{
    private static readonly string SourceDir = ConformanceStore.OutputDirectory;

    /// <summary>Dialect -> node type name -> reason: nodes that a dialect rejects on purpose (<c>rejected-for-dialect</c>). Key <c>*</c> applies to all dialects.</summary>
    private static Dictionary<string, Dictionary<string, string>> RejectedNodes() =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
            File.ReadAllText(Path.Combine(SourceDir, "rejected-nodes.json")), ConformanceStore.Json)!;

    private static ISet<string> RejectedFor(string dialect)
    {
        var all = RejectedNodes();
        var set = new HashSet<string>();
        if (all.TryGetValue("*", out var common)) set.UnionWith(common.Keys);
        if (all.TryGetValue(dialect, out var own)) set.UnionWith(own.Keys);
        return set;
    }

    private static (HashSet<string> Accepted, HashSet<string> Rejected) NodesPerOutcome(string dialect)
    {
        var accepted = new HashSet<string>();
        var rejected = new HashSet<string>();
        foreach (var c in ConformanceStore.Load(dialect).Cases)
        {
            var types = new HashSet<Type>();
            ConformanceVerifier.CollectNodeTypes(ConformanceRunner.Run(c.Sql, c.Group, ConformanceDialects.Parse(dialect)).Statement, types);
            (c.Outcome == "ok" ? accepted : rejected).UnionWith(types.Select(t => t.Name));
        }
        return (accepted, rejected);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void EveryDialect_HasAtLeast300AcceptedCases(string dialect)
    {
        var cases = ConformanceStore.Load(dialect).Cases;
        Assert.True(cases.Count(c => c.Outcome == "ok") >= ConformanceDialects.MinCasesPerDialect,
            $"{dialect}: {cases.Count(c => c.Outcome == "ok")} accepted cases, at least {ConformanceDialects.MinCasesPerDialect} are required.");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void EveryCorpusInput_HasAGoldenEntry_InEveryDialect(string dialect)
    {
        var corpus = ConformanceStore.LoadCorpus(SourceDir);
        var cases = ConformanceStore.Load(dialect).Cases;
        Assert.Equal(corpus.Select(i => (i.Id, i.Group, i.Sql)), cases.Select(c => (c.Id, c.Group, c.Sql)));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Entries_AreWellFormed(string dialect)
    {
        var notes = ConformanceStore.LoadNotes(SourceDir);
        foreach (var c in ConformanceStore.Load(dialect).Cases)
        {
            if (c.Outcome == "ok")
            {
                Assert.False(string.IsNullOrWhiteSpace(c.Expected), $"{c.Id}: accepted entry without expected SQL");
                Assert.Null(c.ExceptionType);
                foreach (var p in c.Parameters) Assert.Contains(p.Marker, c.Expected!, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal("rejected", c.Outcome);
                Assert.False(string.IsNullOrWhiteSpace(c.ExceptionType), $"{c.Id}: rejection without exception type");
                Assert.Null(c.Expected);
            }
            var expectedNote = notes.TryGetValue(c.Id, out var byDialect) ? byDialect.GetValueOrDefault(dialect) ?? byDialect.GetValueOrDefault("*") : null;
            Assert.Equal(expectedNote, c.SemanticNote);
        }
    }

    [Fact]
    public void SemanticNotes_ReferenceExistingInputs_AndKnownDialects()
    {
        var ids = ConformanceStore.LoadCorpus(SourceDir).Select(i => i.Id).ToHashSet();
        var dialects = ConformanceDialects.All.Select(d => d.Name).Append("*").ToHashSet();
        foreach (var (id, byDialect) in ConformanceStore.LoadNotes(SourceDir))
        {
            Assert.Contains(id, ids);
            foreach (var (d, text) in byDialect)
            {
                Assert.Contains(d, dialects);
                Assert.False(string.IsNullOrWhiteSpace(text));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void EveryNode_HasFixtureOrRejection_PerDialect(string dialect)
    {
        var (accepted, _) = NodesPerOutcome(dialect);
        var uncovered = ConformanceVerifier.Uncovered(ConformanceVerifier.RequiredNodeTypes(), accepted, RejectedFor(dialect));
        Assert.True(uncovered.Count == 0, $"{dialect}: nodes without a fixture or a rejected-for-dialect entry: {string.Join(", ", uncovered)}");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void RejectedNodeEntries_AreNeeded_AndBackedByARejectedFixture(string dialect)
    {
        var (accepted, rejected) = NodesPerOutcome(dialect);
        var required = ConformanceVerifier.RequiredNodeTypes().Select(t => t.Name).ToHashSet();
        foreach (var node in RejectedFor(dialect))
        {
            Assert.Contains(node, required);
            Assert.DoesNotContain(node, accepted);   // stale: the dialect accepts it now, so the entry must go
            Assert.Contains(node, rejected);         // the rejection must be demonstrated by a fixture
        }
    }

    // ---- negative controls: the checks above must fail on deliberately broken input ----

    [Fact]
    public void NegativeControl_NodeCoverage_FlagsAMissingFixture()
    {
        var covered = ConformanceVerifier.RequiredNodeTypes().Select(t => t.Name).ToHashSet();
        covered.Remove(nameof(SubscriptExpression));
        var missing = ConformanceVerifier.Uncovered(ConformanceVerifier.RequiredNodeTypes(), covered, new HashSet<string>());
        Assert.Equal(new[] { nameof(SubscriptExpression) }, missing);
        Assert.Empty(ConformanceVerifier.Uncovered(ConformanceVerifier.RequiredNodeTypes(), covered, new HashSet<string> { nameof(SubscriptExpression) }));
    }

    [Fact]
    public void NegativeControl_Verifier_FlagsEveryKindOfDrift()
    {
        var good = ConformanceRunner.Run("SELECT a FROM t WHERE b = 'x'", "select", TargetSqlDialect.PostgreSql);
        var fixture = new ConformanceFixture("c", "select", "SELECT a FROM t WHERE b = 'x'", good.Outcome, good.Sql, good.Parameters, good.ExceptionType, null);
        Assert.Null(ConformanceVerifier.Mismatch(fixture, good));
        Assert.NotNull(ConformanceVerifier.Mismatch(fixture with { Expected = fixture.Expected + " " }, good));
        Assert.NotNull(ConformanceVerifier.Mismatch(fixture with { Parameters = Array.Empty<ExpectedParameter>() }, good));
        Assert.NotNull(ConformanceVerifier.Mismatch(fixture with { Parameters = new[] { fixture.Parameters[0] with { Value = "y" } } }, good));
        Assert.NotNull(ConformanceVerifier.Mismatch(fixture with { Outcome = "rejected", Expected = null, ExceptionType = "SecurityException" }, good));
    }

    [Fact]
    public void NegativeControl_SqlServerAndPostgreSql_Differ()
    {
        // The matrix is only evidence if the dialects are told apart: the same input must yield different text per dialect.
        var pg = ConformanceRunner.Run("SELECT a FROM t ORDER BY a LIMIT 5", "select", TargetSqlDialect.PostgreSql);
        var ms = ConformanceRunner.Run("SELECT a FROM t ORDER BY a LIMIT 5", "select", TargetSqlDialect.SqlServer);
        Assert.NotEqual(pg.Sql, ms.Sql);
    }

    [Fact]
    public void PendingDialectExemption_AppliesOnlyWhileTheCapabilityEntryIsMissing()
    {
        foreach (var (_, dialect) in ConformanceDialects.All)
        {
            var pending = ConformanceVerifier.IsPending(dialect);
            var canGenerate = ConformanceRunner.Run("SELECT a FROM t", "select", dialect).Outcome == "ok";
            Assert.NotEqual(pending, canGenerate);
        }
    }

    public static IEnumerable<object[]> Dialects() => ConformanceDialects.Names();
}
