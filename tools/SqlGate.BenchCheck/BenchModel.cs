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
public sealed record BaselineBucket(double LegacyAllocatedBytesPerOp, double AstAllocatedBytesPerOp);

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

    /// <summary>Buckets that must be present; a missing bucket fails the gate (fail closed).</summary>
    public IReadOnlyList<string> RequiredBuckets { get; init; } = ["0.5KB", "4KB", "64KB"];
}

public sealed record BenchViolation(string Rule, string Bucket, string Message);

public sealed record BenchVerdict(IReadOnlyList<BenchViolation> Violations)
{
    public bool Passed => Violations.Count == 0;
}

/// <summary>The benchmark results or the baseline cannot be read; the tool maps this to exit code 2 (fail closed).</summary>
public sealed class BenchCheckInputException(string message, Exception? inner = null) : Exception(message, inner);
