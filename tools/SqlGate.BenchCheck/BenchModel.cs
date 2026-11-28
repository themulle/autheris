namespace SqlGate.BenchCheck;

/// <summary>The engine a benchmark measured: the legacy token-stream rewriter or the governed AST compiler (cold, cache miss).</summary>
public enum BenchEngine
{
    Legacy,
    Ast
}

/// <summary>One measured (bucket, engine) pair of the G8 benchmark. Times are nanoseconds per compiled query.</summary>
public sealed record BenchSample(
    string Bucket,
    BenchEngine Engine,
    double MeanNs,
    double P95Ns,
    double P99Ns,
    double AllocatedBytesPerOp,
    double Gen2CollectionsPerThousandOps);

/// <summary>Frozen allocation numbers of one bucket (hardware independent, so they can be compared across machines).</summary>
public sealed record BaselineBucket(double LegacyAllocatedBytesPerOp, double AstAllocatedBytesPerOp, double? LegacyGen2CollectionsPerThousandOps = null);

/// <summary>The content of <c>benchmarks/baselines/sql-compiler-legacy.json</c>.</summary>
public sealed record BenchBaseline(IReadOnlyDictionary<string, BaselineBucket> Buckets);

/// <summary>G8 thresholds (plan 9.3). Time is compared by ratio on the same runner; allocation is compared by bytes.</summary>
public sealed record BenchThresholds
{
    public double MaxTimeRatio { get; init; } = 1.5;

    public double MaxAllocationRatio { get; init; } = 1.25;

    public double MaxAllocationRegressionAgainstBaseline { get; init; } = 0.10;

    public double MaxAbsoluteP99Ns { get; init; } = 2_000_000;

    /// <summary>Buckets that must stay under <see cref="MaxAbsoluteP99Ns"/> (plan: up to 4 KB).</summary>
    public IReadOnlySet<string> AbsoluteP99Buckets { get; init; } = new HashSet<string>(StringComparer.Ordinal) { "0.5KB", "4KB" };

    /// <summary>
    /// Buckets that must not trigger any Gen2 collection (plan 9.3, amended by the orchestrator decision 2026-10-11). In the other buckets a query
    /// is already on the large object heap as UTF-16, so the AST Gen2 count is compared with legacy (at most <see cref="MaxGen2Ratio"/>).
    /// </summary>
    public IReadOnlySet<string> ZeroGen2Buckets { get; init; } = new HashSet<string>(StringComparer.Ordinal) { "0.5KB", "4KB" };

    public double MaxGen2Ratio { get; init; } = 1.25;

    /// <summary>Buckets that must be present; a missing bucket fails the gate (fail closed).</summary>
    public IReadOnlyList<string> RequiredBuckets { get; init; } = ["0.5KB", "4KB", "64KB"];

    /// <summary>
    /// ARCH-MNT-15-FU-01: the cold-cache parse (ANTLR cap forced to 1) may be at most this multiple of the warm P99. A ratio, so it does not depend
    /// on the runner speed in principle, but the warm denominator (23 to 62 us) moves with the runner CPU while the cold parse stays within 1.8 to 3.7 ms.
    /// Value: 140.0 = about 1.5 x the highest recorded nightly ratio. Recorded CI nightly ratios (2026-11-18 to 2026-11-27, 7 runs): 55.1, 60.9, 60.9,
    /// 77.9, 81.5, 87.5 and 95.4 (run 45984460386, which failed the earlier limit of 93.0, calibrated from a single sample of 61.77). The limit still
    /// fails a real cold-path regression (cold parse more than about 1.5x slower than the recorded worst case). Raise it only together with a new recorded measurement (SC-07).
    /// </summary>
    public double MaxColdWarmP99Ratio { get; init; } = 140.0;
}

public sealed record BenchViolation(string Rule, string Bucket, string Message);

public sealed record BenchVerdict(IReadOnlyList<BenchViolation> Violations)
{
    public bool Passed => Violations.Count == 0;
}

/// <summary>The benchmark results or the baseline cannot be read; the tool maps this to exit code 2 (fail closed).</summary>
public sealed class BenchCheckInputException(string message, Exception? inner = null) : Exception(message, inner);
