namespace Autheris.Tests.SqlCompilerGate;

using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;

/// <summary>
/// WAVE3-SQLGATE-T-01 (SC-04, SC-05): the G7 mutation gate cannot be weakened silently. The configuration keeps the break threshold and the
/// security globs; the workflow runs G7 in its own job whose timeout/cancel ends as failure and whose evidence is always uploaded.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class StrykerConfigGuardTests
{
    private const string G7Job = "sql-compiler-gate-g7";

    private static readonly string[] SecurityGlobs =
    [
        "**/Ast/Visitors/AstSecurityVisitor*.cs",
        "**/Ast/Visitors/AstSimplificationVisitor.cs",
        "**/Ast/Security/*.cs",
        "**/Ast/Emit/*.cs"
    ];

    [GeneratedRegex(@"^      - name:\s*(.+?)\s*$")]
    private static partial Regex StepHeader();

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) return dir.FullName;
        }

        throw new InvalidOperationException("Autheris.sln was not found.");
    }

    private static JsonElement Config()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "stryker-config.json")));
        return doc.RootElement.GetProperty("stryker-config").Clone();
    }

    private static string[] Workflow() => File.ReadAllLines(Path.Combine(Root(), ".github", "workflows", "sql-compiler-gate-nightly.yml"));

    private static string[] Job(string name)
    {
        var lines = Workflow();
        int start = Array.FindIndex(lines, l => l.TrimEnd() == $"  {name}:");
        start.ShouldBeGreaterThanOrEqualTo(0, $"job {name} must exist");
        int end = Array.FindIndex(lines, start + 1, l => Regex.IsMatch(l, @"^  [A-Za-z0-9_-]+:\s*(#.*)?$"));
        return lines[(start + 1)..(end < 0 ? lines.Length : end)];
    }

    private static int JobTimeout(string[] job) =>
        int.Parse(job.Select(l => Regex.Match(l, @"^    timeout-minutes:\s*(\d+)")).First(m => m.Success).Groups[1].Value);

    private static string[] Step(string[] job, string namePart)
    {
        int start = Array.FindIndex(job, l => StepHeader().Match(l) is { Success: true } m && m.Groups[1].Value.Contains(namePart, StringComparison.Ordinal));
        start.ShouldBeGreaterThanOrEqualTo(0, $"step '{namePart}' must exist");
        int end = Array.FindIndex(job, start + 1, l => StepHeader().IsMatch(l));
        return job[start..(end < 0 ? job.Length : end)];
    }

    [Fact]
    public void StrykerConfig_BreakThresholdAndSecurityGlobs_Unchanged()
    {
        var config = Config();

        config.GetProperty("thresholds").GetProperty("break").GetInt32().ShouldBe(85);
        var mutate = config.GetProperty("mutate").EnumerateArray().Select(e => e.GetString()!).ToArray();
        foreach (string glob in SecurityGlobs)
        {
            mutate.ShouldContain(glob);
        }

        mutate.ShouldNotContain(g => g.StartsWith('!'), "no exclusion may remove security files from the scope");
    }

    [Fact]
    public void StrykerConfig_NoMutantFiltersAndPerTestCoverage()
    {
        var config = Config();

        config.TryGetProperty("ignore-mutations", out _).ShouldBeFalse();
        config.TryGetProperty("ignore-methods", out _).ShouldBeFalse();
        if (config.TryGetProperty("mutation-level", out var level)) level.GetString().ShouldBe("Standard");
        config.GetProperty("coverage-analysis").GetString().ShouldBe("perTest");
        config.GetProperty("concurrency").GetInt32().ShouldBe(4);
    }

    [Fact]
    public void Workflow_G7Job_TimeoutEndsAsFailure_AndEvidenceIsAlwaysKept()
    {
        var job = Job(G7Job);
        int jobTimeout = JobTimeout(job);

        job.ShouldNotContain(l => l.Contains("continue-on-error", StringComparison.Ordinal), "G7 must never be advisory (SC-05)");

        var stryker = Step(job, "G7 mutation testing");
        var match = stryker.Select(l => Regex.Match(l, @"^        timeout-minutes:\s*(\d+)")).FirstOrDefault(m => m.Success);
        match.ShouldNotBeNull("the G7 step needs its own timeout so that a timeout is a step failure, not a cancelled job");
        int stepTimeout = int.Parse(match.Groups[1].Value);
        stepTimeout.ShouldBeLessThan(jobTimeout);
        string.Join('\n', stryker).ShouldContain("--log-to-file");
        string.Join('\n', stryker).ShouldContain("--break-at 0");   // shards never gate; the aggregate enforces 85 on the summed counts

        Step(job, "G7 shard result").ShouldContain(l => Regex.IsMatch(l, @"^        if:\s*always\(\)\s*$"));
        var upload = Step(job, "Upload");
        upload.ShouldContain(l => Regex.IsMatch(l, @"^        if:\s*always\(\)\s*$"));
        string.Join('\n', upload).ShouldContain("StrykerOutput");
    }

    [Fact]
    public void Workflow_G7Job_AppliesTheShardScopeBeforeStryker_AndAggregateHasAFloorAndBreak()
    {
        var job = Job(G7Job);
        int scope = Array.FindIndex(job, l => l.Contains("g7-scope.sh --apply", StringComparison.Ordinal));
        int stryker = Array.FindIndex(job, l => l.Contains("dotnet-stryker", StringComparison.Ordinal));

        scope.ShouldBeGreaterThanOrEqualTo(0);
        stryker.ShouldBeGreaterThan(scope);
        job[scope].ShouldContain("--shard");

        // SC-06: the aggregate runs on always(), needs every shard, takes the floor from an explicit env input and enforces break 85.
        var aggregate = Job(G7Job + "-aggregate");
        aggregate.ShouldContain(l => l.Trim() == "needs: " + G7Job);
        aggregate.ShouldContain(l => Regex.IsMatch(l, @"^    if:\s*always\(\)\s*$"));
        aggregate.ShouldNotContain(l => l.Contains("continue-on-error", StringComparison.Ordinal));
        string verdict = string.Join('\n', Step(aggregate, "G7 aggregate verdict"));
        verdict.ShouldContain("--break-at 85");
        verdict.ShouldContain("--needs-result");
        verdict.ShouldContain("--floor \"${G7_MUTANT_FLOOR}\"");
        Regex.IsMatch(string.Join('\n', Workflow()), @"^  G7_MUTANT_FLOOR:\s*'[1-9]\d*'", RegexOptions.Multiline).ShouldBeTrue();
        var shards = Regex.Match(string.Join('\n', Workflow()), @"^  G7_SHARDS:\s*'(\d+)'", RegexOptions.Multiline);
        shards.Success.ShouldBeTrue();
        var matrix = Regex.Match(string.Join('\n', job), @"shard:\s*\[([0-9, ]+)\]");
        matrix.Success.ShouldBeTrue();
        matrix.Groups[1].Value.Split(',').Select(x => int.Parse(x.Trim())).ShouldBe(Enumerable.Range(0, int.Parse(shards.Groups[1].Value)));
    }

    [Fact]
    public void Workflow_GateJob_HasNoStryker_AndAShorterTimeout()
    {
        var gate = Job("sql-compiler-gate-nightly");

        gate.Where(l => !l.TrimStart().StartsWith('#')).ShouldNotContain(l => l.Contains("stryker", StringComparison.OrdinalIgnoreCase));
        JobTimeout(gate).ShouldBeLessThanOrEqualTo(60);
    }

    [Fact]
    public void Workflow_G8Benchmark_IncludesParserCacheBenchmarks()
    {
        string text = string.Join('\n', Job("sql-compiler-gate-nightly"));

        text.ShouldContain("*SqlParserCache*");
    }
}
