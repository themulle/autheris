namespace SqlGate.BenchCheck;

/// <summary>
/// Evaluates the G8 thresholds (plan 9.3). Before cutover the legacy and AST samples come from the same benchmark job and are compared by
/// ratio. After cutover there is no legacy engine to measure, so the allocation is compared with the frozen baseline. Anything missing,
/// not finite or not positive is a violation: the gate fails closed.
/// </summary>
public static class BenchEvaluator
{
    public static BenchVerdict Evaluate(IReadOnlyList<BenchSample> samples, BenchBaseline? baseline, BenchThresholds thresholds)
    {
        var violations = new List<BenchViolation>();
        if (samples.Count == 0)
        {
            violations.Add(new BenchViolation("missing-data", "*", "The benchmark results contain no sample."));
            return new BenchVerdict(violations);
        }

        bool compareMode = samples.Any(s => s.Engine == BenchEngine.Legacy);
        foreach (string bucket in thresholds.RequiredBuckets)
        {
            var ast = samples.FirstOrDefault(s => s.Bucket == bucket && s.Engine == BenchEngine.Ast);
            if (ast is null)
            {
                violations.Add(new BenchViolation("missing-data", bucket, "No AST sample for this bucket."));
                continue;
            }

            if (!IsPositive(ast.P95Ns) || !IsPositive(ast.P99Ns) || !IsPositive(ast.AllocatedBytesPerOp) || !IsFinite(ast.Gen2CollectionsPerThousandOps))
            {
                violations.Add(new BenchViolation("missing-data", bucket, "The AST sample has a non-finite or non-positive value."));
                continue;
            }

            CheckGen2(bucket, ast, samples, baseline, compareMode, thresholds, violations);

            if (thresholds.AbsoluteP99Buckets.Contains(bucket) && ast.P99Ns > thresholds.MaxAbsoluteP99Ns)
            {
                violations.Add(new BenchViolation("absolute-p99", bucket, $"AST P99 {ast.P99Ns / 1e6:F3} ms exceeds {thresholds.MaxAbsoluteP99Ns / 1e6:F3} ms."));
            }

            if (compareMode)
            {
                CompareWithLegacy(bucket, ast, samples, thresholds, violations);
            }
            else
            {
                CompareWithBaseline(bucket, ast, baseline, thresholds, violations);
            }
        }

        return new BenchVerdict(violations);
    }

    private static void CheckGen2(string bucket, BenchSample ast, IReadOnlyList<BenchSample> samples, BenchBaseline? baseline, bool compareMode, BenchThresholds thresholds, List<BenchViolation> violations)
    {
        double astGen2 = ast.Gen2CollectionsPerThousandOps;
        if (thresholds.ZeroGen2Buckets.Contains(bucket))
        {
            if (astGen2 > 0)
            {
                violations.Add(new BenchViolation("gen2-loh", bucket, $"AST triggered {astGen2:G4} Gen2 collections per 1000 operations (limit 0)."));
            }

            return;
        }

        // Larger buckets: the legacy count is the reference. No reference at all (and any Gen2) fails closed.
        double? reference = compareMode
            ? samples.FirstOrDefault(s => s.Bucket == bucket && s.Engine == BenchEngine.Legacy)?.Gen2CollectionsPerThousandOps
            : baseline?.Buckets.GetValueOrDefault(bucket)?.LegacyGen2CollectionsPerThousandOps;
        if (reference is not { } legacyGen2 || !double.IsFinite(legacyGen2) || legacyGen2 < 0)
        {
            if (astGen2 > 0)
            {
                violations.Add(new BenchViolation("gen2-loh", bucket, $"AST triggered {astGen2:G4} Gen2 collections per 1000 operations and there is no legacy Gen2 reference to compare with."));
            }

            return;
        }

        if (astGen2 > legacyGen2 * thresholds.MaxGen2Ratio)
        {
            violations.Add(new BenchViolation("gen2-loh", bucket, $"AST triggered {astGen2:G4} Gen2 collections per 1000 operations, legacy {legacyGen2:G4} (limit {thresholds.MaxGen2Ratio:F2}x)."));
        }
    }

    private static void CompareWithLegacy(string bucket, BenchSample ast, IReadOnlyList<BenchSample> samples, BenchThresholds thresholds, List<BenchViolation> violations)
    {
        var legacy = samples.FirstOrDefault(s => s.Bucket == bucket && s.Engine == BenchEngine.Legacy);
        if (legacy is null || !IsPositive(legacy.P95Ns) || !IsPositive(legacy.P99Ns) || !IsPositive(legacy.AllocatedBytesPerOp))
        {
            violations.Add(new BenchViolation("missing-data", bucket, "No usable legacy sample for this bucket."));
            return;
        }

        CheckRatio("time-ratio-p95", bucket, ast.P95Ns, legacy.P95Ns, thresholds.MaxTimeRatio, violations);
        CheckRatio("time-ratio-p99", bucket, ast.P99Ns, legacy.P99Ns, thresholds.MaxTimeRatio, violations);
        CheckRatio("allocation-ratio", bucket, ast.AllocatedBytesPerOp, legacy.AllocatedBytesPerOp, thresholds.MaxAllocationRatio, violations);
    }

    private static void CompareWithBaseline(string bucket, BenchSample ast, BenchBaseline? baseline, BenchThresholds thresholds, List<BenchViolation> violations)
    {
        if (baseline is null)
        {
            violations.Add(new BenchViolation("missing-data", bucket, "No legacy sample and no baseline: nothing to compare the AST allocation with."));
            return;
        }

        if (!baseline.Buckets.TryGetValue(bucket, out var frozen) || !IsPositive(frozen.LegacyAllocatedBytesPerOp) || !IsPositive(frozen.AstAllocatedBytesPerOp))
        {
            violations.Add(new BenchViolation("missing-data", bucket, "The baseline has no usable entry for this bucket."));
            return;
        }

        CheckRatio("allocation-ratio", bucket, ast.AllocatedBytesPerOp, frozen.LegacyAllocatedBytesPerOp, thresholds.MaxAllocationRatio, violations);
        double limit = frozen.AstAllocatedBytesPerOp * (1 + thresholds.MaxAllocationRegressionAgainstBaseline);
        if (ast.AllocatedBytesPerOp > limit)
        {
            violations.Add(new BenchViolation("allocation-regression", bucket,
                $"AST allocates {ast.AllocatedBytesPerOp:F0} B per operation, more than {thresholds.MaxAllocationRegressionAgainstBaseline:P0} above the baseline {frozen.AstAllocatedBytesPerOp:F0} B."));
        }
    }

    private static void CheckRatio(string rule, string bucket, double ast, double legacy, double max, List<BenchViolation> violations)
    {
        double ratio = ast / legacy;
        if (ratio > max)
        {
            violations.Add(new BenchViolation(rule, bucket, $"AST/legacy is {ratio:F2}x, limit {max:F2}x."));
        }
    }

    private static bool IsFinite(double value) => double.IsFinite(value);

    private static bool IsPositive(double value) => double.IsFinite(value) && value > 0;
}
