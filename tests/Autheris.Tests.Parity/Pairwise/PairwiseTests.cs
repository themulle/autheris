using Autheris.Tests.Parity.Infrastructure;
using Shouldly;

namespace Autheris.Tests.Parity.Pairwise;

/// <summary>Generator and model tests (no host): determinism, pair coverage, budget, constraints (D-14).</summary>
[Trait("Category", "Parity")]
public sealed class PairwiseTests : ParityTestBase
{
    private const int Seed = 20261023;

    [Fact]
    public void Pairwise_SameSeedSameCases()
    {
        var first = PairwiseModel.Cases(Seed).Select(c => c.Id).ToList();
        var second = PairwiseModel.Cases(Seed).Select(c => c.Id).ToList();
        second.ShouldBe(first);
        PairwiseModel.Cases(Seed + 1).Select(c => c.Id).ToList().ShouldNotBe(first);
    }

    [Fact]
    public void Pairwise_SeedFromTextIsStableAndRejectsGarbage()
    {
        PairwiseGenerator.SeedFrom("20261023").ShouldBe(20261023);
        PairwiseGenerator.SeedFrom("4294967297").ShouldBe(PairwiseGenerator.SeedFrom("4294967297"));
        Should.Throw<ArgumentException>(() => PairwiseGenerator.SeedFrom("abc"));
        Should.Throw<ArgumentException>(() => PairwiseGenerator.SeedFrom("-1"));
    }

    [Theory]
    [InlineData(20261023)]
    [InlineData(1)]
    [InlineData(987654321)]
    public void Pairwise_CoversEveryPair(int seed)
    {
        PairwiseGenerator.UncoveredPairs(PairwiseModel.BaseFactors, PairwiseGenerator.Generate(PairwiseModel.BaseFactors, seed)).ShouldBeEmpty();
        var cases = PairwiseModel.Cases(seed);
        PairwiseGenerator.UncoveredPairs(PairwiseModel.AllFactors, PairwiseModel.AsIndexRows(cases)).ShouldBeEmpty();
    }

    [Fact]
    public void Pairwise_PostgresDatePartGroupingIsRenderedNotPending()
    {
        PairwiseExecutor.RenderGroupKey("year", PairwiseModel.Postgres).ShouldBe("EXTRACT(YEAR FROM sale_date)");
        PairwiseExecutor.RenderGroupKey("month", PairwiseModel.Postgres)!.ShouldContain("to_char");
        PairwiseExecutor.RenderGroupKey("day", PairwiseModel.Postgres)!.ShouldContain("YYYY-MM-DD");
        PairwiseExecutor.RenderGroupKey("year", PairwiseModel.Sqlite)!.ShouldContain("strftime");
        PairwiseExecutor.RenderGroupKey("none", PairwiseModel.Postgres).ShouldBeNull();
    }

    [Fact]
    public void Pairwise_CaseCountWithinBudget()
    {
        var cases = PairwiseModel.Cases(Seed);
        cases.Count.ShouldBeLessThanOrEqualTo(400);
        cases.Select(c => c.Id).Distinct().Count().ShouldBe(cases.Count);
        // far fewer than the full cartesian product (6 x 6 x 9 x 6 x 3 = 5832)
        cases.Count.ShouldBeLessThan(PairwiseModel.AllFactors.Aggregate(1, (n, f) => n * f.Values.Count) / 10);
        Console.WriteLine($"pairwise: {cases.Count} cases, seed {ParityEnvironment.Seed}");
    }

    [Fact]
    public void Pairwise_ForbiddenCombinationsAreNeverGenerated()
    {
        var factors = new[] { new Factor("a", ["x", "y", "z"]), new Factor("b", ["p", "q"]), new Factor("c", ["1", "2", "3"]) };
        // a=x with c=3 is invalid
        bool Forbidden(int[] t) => t[0] == 0 && t[2] == 2;
        var tests = PairwiseGenerator.Generate(factors, 7, Forbidden);
        tests.Any(Forbidden).ShouldBeFalse();
        PairwiseGenerator.UncoveredPairs(factors, tests, Forbidden).ShouldBeEmpty();
    }

    [Fact]
    public void Pairwise_UncoveredPairsDetectsAMissingPair()
    {
        var factors = new[] { new Factor("a", ["x", "y"]), new Factor("b", ["p", "q"]) };
        PairwiseGenerator.UncoveredPairs(factors, [[0, 0], [1, 1], [0, 1]]).ShouldBe(["a=y, b=p"]);
    }

    [Fact]
    public void Pairwise_GeneratorRejectsASingleFactor() =>
        Should.Throw<ArgumentException>(() => PairwiseGenerator.Generate([new Factor("a", ["x"])], 1));

    [Fact]
    public void Pairwise_ConstraintsMarkTypeInvalidCombinations()
    {
        PairwiseModel.IsInvalidCombination("sum", "string").ShouldBeTrue();
        PairwiseModel.IsInvalidCombination("avg", "date").ShouldBeTrue();
        PairwiseModel.IsInvalidCombination("min", "bool").ShouldBeTrue();
        PairwiseModel.IsInvalidCombination("sum", "decimal").ShouldBeFalse();
        PairwiseModel.IsInvalidCombination("count_distinct", "string").ShouldBeFalse();
        PairwiseModel.IsInvalidCombination("min", "date").ShouldBeFalse();
    }

    [Fact]
    public void Pairwise_EveryCaseHasAnOracleExpectation()
    {
        foreach (var c in PairwiseModel.Cases(Seed).Where(c => c.Dialect == PairwiseModel.Sqlite && !PairwiseModel.IsInvalidCombination(c.Aggregate, c.Type)))
        {
            var (columns, actual, rows) = PairwiseExecutor.Expected(c, web: true);
            actual.Length.ShouldBe(columns.Length, c.Id);
            rows.ShouldAllBe(r => r.Length == columns.Length, c.Id);
        }
    }
}
