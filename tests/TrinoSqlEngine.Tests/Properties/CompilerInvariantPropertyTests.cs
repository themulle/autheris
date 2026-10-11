using Xunit;
using Xunit.Abstractions;

namespace TrinoSqlEngine.Tests.Properties;

/// <summary>
/// WP-X0u: generative tests of the compiler security invariants (plan 9.3 G3/G5, INV-1..INV-10). Generated Trino statements
/// (joins, set operations, CTEs, subqueries in every position, window functions, pagination, DML, hostile literals) are compiled
/// for every governed dialect and the emitted text and parameters are checked by independent oracles. Seeds are pinned; one
/// logged random seed is added; nothing is retried (M-5).
/// </summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public class CompilerInvariantPropertyTests(ITestOutputHelper output)
{
    private static readonly CompileFn Production = ProductionCompiler.Compile;

    [Fact]
    public void EmittedSql_SatisfiesEverySecurityInvariant_ForGeneratedStatements()
    {
        output.WriteLine($"profile={GateProfile.Name} seeds={string.Join(",", GateProfile.PinnedSeeds)} randomSeed={GateProfile.RandomSeed}");
        var result = PropertyRunner.Run(StmtCase.Gen(), GateProfile.CasesPerProperty, c => Oracles.CheckCase(c, Production));
        Report(result);
        result.ThrowIfFailed(nameof(EmittedSql_SatisfiesEverySecurityInvariant_ForGeneratedStatements));

        // The generator must not be vacuous: most cases compile, and the rejections are typed.
        Assert.True(result.Compiled >= result.Cases / 2, $"only {result.Compiled} of {result.Cases} generated statements compiled");
    }

    [Fact]
    public void Compile_IsDeterministic_OnAColdEngine()
    {
        var result = PropertyRunner.Run(StmtCase.Gen(), GateProfile.CasesPerProperty / 2,
            c => (c.Sql, c.Ctx, Oracles.CheckDeterminism(c, ProductionCompiler.CompileCold), null, null));
        result.ThrowIfFailed(nameof(Compile_IsDeterministic_OnAColdEngine));
    }

    [Fact]
    public void Compile_DoesNotDependOnKeywordCaseOrWhitespace()
    {
        var result = PropertyRunner.Run(StmtCase.Gen(), GateProfile.CasesPerProperty / 2,
            c => (c.Sql, c.Ctx, Oracles.CheckIdentity(c, Production), null, null));
        result.ThrowIfFailed(nameof(Compile_DoesNotDependOnKeywordCaseOrWhitespace));
    }

    [Fact]
    public void ParseRenderParse_IsTheIdentity_ForGeneratedQueries()
    {
        // G3: Trino text -> AST -> Trino text (test-only renderer) -> AST must be structurally equal. A difference means the
        // builder lost or invented structure. Statements the builder rejects, and nodes the renderer does not print, are counted.
        int built = 0, rendered = 0;
        var result = PropertyRunner.Run(StmtCase.Gen(), GateProfile.CasesPerProperty, c =>
        {
            var (violations, wasBuilt, wasRendered) = RoundTrip.Check(c, TrinoSourceRenderer.Render);
            built += wasBuilt ? 1 : 0;
            rendered += wasRendered ? 1 : 0;
            return (c.Sql, c.Ctx, violations, null, null);
        });
        output.WriteLine($"cases={result.Cases} built={built} rendered={rendered}");
        result.ThrowIfFailed(nameof(ParseRenderParse_IsTheIdentity_ForGeneratedQueries));
        Assert.True(rendered >= built * 8 / 10, $"the renderer covers only {rendered} of {built} built statements");
    }

    private void Report(RunResult r)
    {
        output.WriteLine($"cases={r.Cases} compiled={r.Compiled} rejected={r.Rejected}");
        foreach (var (type, count) in r.RejectionTypes.OrderByDescending(p => p.Value))
        {
            output.WriteLine($"  rejected by {type}: {count}");
        }
    }
}
