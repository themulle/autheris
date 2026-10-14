using System.Globalization;
using SqlGate.BenchCheck;

// G8 threshold check (plan 9.3).
// Usage: SqlGate.BenchCheck <results-dir> <baseline-dir> [--freeze --machine <text>]
//   Exit codes: 0 all thresholds met, 1 a threshold is violated (or data is missing), 2 usage or unreadable input.
//   --freeze writes <baseline-dir>/sql-compiler-legacy.json from the results (done once in the cutover PR).
const string BaselineFile = "sql-compiler-legacy.json";

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: SqlGate.BenchCheck <results-dir> <baseline-dir> [--freeze --machine <text>]");
    return 2;
}

string resultsDir = args[0];
string baselineDir = args[1];
bool freeze = args.Contains("--freeze");
int machineIndex = Array.IndexOf(args, "--machine");
string machine = machineIndex >= 0 && machineIndex + 1 < args.Length ? args[machineIndex + 1] : "unspecified";

try
{
    if (!Directory.Exists(resultsDir))
    {
        throw new BenchCheckInputException($"The results directory '{resultsDir}' does not exist.");
    }

    var reports = Directory.GetFiles(resultsDir, "*SqlCompilerBenchmarks-report-full*.json", SearchOption.AllDirectories);
    if (reports.Length == 0)
    {
        throw new BenchCheckInputException($"No SqlCompilerBenchmarks JSON report below '{resultsDir}'.");
    }

    // Several exports (for example a re-run into the same directory) are merged; the newest file wins per (bucket, engine).
    var samples = new Dictionary<(string, BenchEngine), BenchSample>();
    foreach (string report in reports.OrderBy(File.GetLastWriteTimeUtc))
    {
        foreach (var sample in BenchResultReader.ReadSamples(File.ReadAllText(report)))
        {
            samples[(sample.Bucket, sample.Engine)] = sample;
        }
    }

    var all = samples.Values.OrderBy(s => s.Bucket, StringComparer.Ordinal).ThenBy(s => s.Engine).ToList();
    foreach (var s in all)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{s.Bucket,-6} {s.Engine,-6} mean {s.MeanNs / 1e3,10:F1} us  p95 {s.P95Ns / 1e3,10:F1} us  p99 {s.P99Ns / 1e3,10:F1} us  alloc {s.AllocatedBytesPerOp / 1024,10:F1} KB  gen2/1000 {s.Gen2CollectionsPerThousandOps:F2}"));
    }

    string baselinePath = Path.Combine(baselineDir, BaselineFile);
    if (freeze)
    {
        Directory.CreateDirectory(baselineDir);
        File.WriteAllText(baselinePath, BenchResultReader.WriteBaseline(all, machine, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        Console.WriteLine($"Baseline written to {baselinePath}.");
        return 0;
    }

    BenchBaseline? baseline = File.Exists(baselinePath) ? BenchResultReader.ReadBaseline(File.ReadAllText(baselinePath)) : null;
    var verdict = BenchEvaluator.Evaluate(all, baseline, new BenchThresholds());
    foreach (var violation in verdict.Violations)
    {
        Console.Error.WriteLine($"G8 violation [{violation.Rule}] {violation.Bucket}: {violation.Message}");
    }

    Console.WriteLine(verdict.Passed ? "G8: all thresholds met." : "G8: FAILED.");
    return verdict.Passed ? 0 : 1;
}
catch (BenchCheckInputException ex)
{
    Console.Error.WriteLine($"G8 input error: {ex.Message}");
    return 2;
}
