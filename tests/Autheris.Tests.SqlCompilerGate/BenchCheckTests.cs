namespace Autheris.Tests.SqlCompilerGate;

using Shouldly;
using SqlGate.BenchCheck;

/// <summary>
/// G8 threshold evaluation (plan 9.3): AST/legacy at most 1.5x for P95 and P99, allocated bytes at most 1.25x, no Gen2 (LOH) up to 64 KB,
/// absolute P99 under 2 ms up to 4 KB; after cutover the allocation is compared with the frozen baseline (more than 10% fails).
/// Missing data fails closed.
/// </summary>
public sealed class BenchCheckTests
{
    private static readonly string[] Buckets = ["0.5KB", "4KB", "64KB"];
    private static readonly BenchThresholds Defaults = new();

    private static BenchSample Legacy(string bucket, double p95 = 100_000, double p99 = 120_000, double alloc = 10_000) =>
        new(bucket, BenchEngine.Legacy, p95 * 0.8, p95, p99, alloc, 0);

    private static BenchSample Ast(string bucket, double p95 = 100_000, double p99 = 120_000, double alloc = 10_000, double gen2 = 0) =>
        new(bucket, BenchEngine.Ast, p95 * 0.8, p95, p99, alloc, gen2);

    private static List<BenchSample> Healthy() =>
        [.. Buckets.SelectMany(b => new[] { Legacy(b), Ast(b) })];

    [Fact]
    public void BenchCheck_ThresholdEvaluation_HealthyResults_Pass()
    {
        BenchEvaluator.Evaluate(Healthy(), null, Defaults).Passed.ShouldBeTrue();
    }

    [Fact]
    public void BenchCheck_ThresholdEvaluation_AtExactlyTheLimits_Passes()
    {
        var samples = Buckets.SelectMany(b => new[] { Legacy(b, 100_000, 100_000, 10_000), Ast(b, 150_000, 150_000, 12_500) }).ToList();
        BenchEvaluator.Evaluate(samples, null, Defaults).Passed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(160_000, 120_000, "time-ratio-p95")]
    [InlineData(100_000, 190_000, "time-ratio-p99")]
    public void BenchCheck_ThresholdEvaluation_TimeRatioAboveLimit_Fails(double p95, double p99, string rule)
    {
        var samples = Buckets.SelectMany(b => new[] { Legacy(b), b == "64KB" ? Ast(b, p95, p99) : Ast(b) }).ToList();
        var verdict = BenchEvaluator.Evaluate(samples, null, Defaults);
        verdict.Passed.ShouldBeFalse();
        verdict.Violations.ShouldContain(v => v.Rule == rule && v.Bucket == "64KB");
    }

    [Fact]
    public void BenchCheck_ThresholdEvaluation_AllocationRatioAboveLimit_Fails()
    {
        var samples = Buckets.SelectMany(b => new[] { Legacy(b), b == "4KB" ? Ast(b, alloc: 12_600) : Ast(b) }).ToList();
        var verdict = BenchEvaluator.Evaluate(samples, null, Defaults);
        verdict.Violations.ShouldContain(v => v.Rule == "allocation-ratio" && v.Bucket == "4KB");
    }

    [Fact]
    public void BenchCheck_ThresholdEvaluation_Gen2Collections_Fail_ForEveryBucket()
    {
        foreach (string bucket in Buckets)
        {
            var samples = Buckets.SelectMany(b => new[] { Legacy(b), b == bucket ? Ast(b, gen2: 0.5) : Ast(b) }).ToList();
            BenchEvaluator.Evaluate(samples, null, Defaults).Violations.ShouldContain(v => v.Rule == "gen2-loh" && v.Bucket == bucket);
        }
    }

    [Theory]
    [InlineData("0.5KB", true)]
    [InlineData("4KB", true)]
    [InlineData("64KB", false)]
    public void BenchCheck_ThresholdEvaluation_AbsoluteP99_AppliesUpTo4Kb(string bucket, bool enforced)
    {
        // The legacy sample is slow as well, so the ratio rules stay green and only the absolute rule can fire.
        var samples = Buckets.SelectMany(b => b == bucket
            ? new[] { Legacy(b, 2_100_000, 2_100_000), Ast(b, 2_100_000, 2_100_000) }
            : new[] { Legacy(b), Ast(b) }).ToList();
        var verdict = BenchEvaluator.Evaluate(samples, null, Defaults);
        verdict.Violations.Any(v => v.Rule == "absolute-p99" && v.Bucket == bucket).ShouldBe(enforced);
    }

    [Fact]
    public void BenchCheck_ThresholdEvaluation_MissingBucket_FailsClosed()
    {
        var samples = Healthy().Where(s => s.Bucket != "64KB").ToList();
        BenchEvaluator.Evaluate(samples, null, Defaults).Violations.ShouldContain(v => v.Rule == "missing-data" && v.Bucket == "64KB");
    }

    [Fact]
    public void BenchCheck_ThresholdEvaluation_NoSamples_FailsClosed()
    {
        BenchEvaluator.Evaluate([], null, Defaults).Passed.ShouldBeFalse();
    }

    [Fact]
    public void BenchCheck_ThresholdEvaluation_LegacyWithoutAst_FailsClosed()
    {
        var samples = Buckets.Select(b => Legacy(b)).ToList();
        var verdict = BenchEvaluator.Evaluate(samples, null, Defaults);
        verdict.Passed.ShouldBeFalse();
        verdict.Violations.ShouldContain(v => v.Rule == "missing-data");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(-1)]
    public void BenchCheck_ThresholdEvaluation_NonsensicalLegacyTiming_FailsClosed(double legacyP95)
    {
        var samples = Buckets.SelectMany(b => new[] { b == "4KB" ? Legacy(b, legacyP95) : Legacy(b), Ast(b) }).ToList();
        BenchEvaluator.Evaluate(samples, null, Defaults).Passed.ShouldBeFalse();
    }

    private static BenchBaseline Baseline(double legacy = 10_000, double ast = 11_000) =>
        new(Buckets.ToDictionary(b => b, _ => new BaselineBucket(legacy, ast)));

    [Fact]
    public void BenchCheck_ThresholdEvaluation_PostCutover_AstWithinTenPercentOfBaseline_Passes()
    {
        var samples = Buckets.Select(b => Ast(b, alloc: 12_100)).ToList();   // 11,000 * 1.10
        BenchEvaluator.Evaluate(samples, Baseline(), Defaults).Passed.ShouldBeTrue();
    }

    [Fact]
    public void BenchCheck_ThresholdEvaluation_PostCutover_AllocationRegressionAboveTenPercent_Fails()
    {
        var samples = Buckets.Select(b => b == "0.5KB" ? Ast(b, alloc: 12_200) : Ast(b)).ToList();
        var verdict = BenchEvaluator.Evaluate(samples, Baseline(), Defaults);
        verdict.Violations.ShouldContain(v => v.Rule == "allocation-regression" && v.Bucket == "0.5KB");
    }

    [Fact]
    public void BenchCheck_ThresholdEvaluation_PostCutover_AllocationAbove125OfFrozenLegacy_Fails()
    {
        var samples = Buckets.Select(b => Ast(b, alloc: 12_600)).ToList();
        var verdict = BenchEvaluator.Evaluate(samples, Baseline(legacy: 10_000, ast: 12_600), Defaults);
        verdict.Violations.ShouldContain(v => v.Rule == "allocation-ratio");
    }

    [Fact]
    public void BenchCheck_ThresholdEvaluation_PostCutover_WithoutBaseline_FailsClosed()
    {
        var samples = Buckets.Select(b => Ast(b)).ToList();
        BenchEvaluator.Evaluate(samples, null, Defaults).Violations.ShouldContain(v => v.Rule == "missing-data");
    }

    [Fact]
    public void BenchCheck_ThresholdEvaluation_PostCutover_BaselineMissingBucket_FailsClosed()
    {
        var baseline = new BenchBaseline(Baseline().Buckets.Where(kv => kv.Key != "4KB").ToDictionary());
        var samples = Buckets.Select(b => Ast(b)).ToList();
        BenchEvaluator.Evaluate(samples, baseline, Defaults).Violations.ShouldContain(v => v.Rule == "missing-data" && v.Bucket == "4KB");
    }

    [Fact]
    public void BenchCheck_ThresholdEvaluation_PostCutover_StillEnforcesGen2AndAbsoluteP99()
    {
        var samples = Buckets.Select(b => b == "4KB" ? Ast(b, p99: 2_100_000, gen2: 1) : Ast(b)).ToList();
        var verdict = BenchEvaluator.Evaluate(samples, Baseline(), Defaults);
        verdict.Violations.ShouldContain(v => v.Rule == "gen2-loh" && v.Bucket == "4KB");
        verdict.Violations.ShouldContain(v => v.Rule == "absolute-p99" && v.Bucket == "4KB");
    }
}
