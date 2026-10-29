namespace Autheris.Tests.SqlCompilerGate;

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;

/// <summary>
/// G7 shard fallback (WAVE3-SQLGATE-SC-06, T-09): deterministic shard assignment, shard-scoped scope injection and the aggregate
/// that sums raw counts. The scripts run as processes; they need bash, git, jq and python3 (all present on the CI runner).
/// </summary>
[Trait("Category", "Security")]
public sealed partial class G7ShardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "g7-shard-" + Guid.NewGuid().ToString("N"));

    public G7ShardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [GeneratedRegex(@"Score: [0-9.]+% \(killed\+timeout \d+ of \d+ valid")]
    private static partial Regex ScoreLine();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) return dir.FullName;
        }

        throw new InvalidOperationException("Autheris.sln was not found.");
    }

    private static (int Code, string Output) Run(string file, string[] args, string cwd, Dictionary<string, string?>? env = null)
    {
        var info = new ProcessStartInfo(file) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) info.ArgumentList.Add(a);
        info.Environment["CI"] = "true";
        info.Environment.Remove("GITHUB_STEP_SUMMARY");
        foreach (var (k, v) in env ?? []) { if (v is null) info.Environment.Remove(k); else info.Environment[k] = v; }
        using var process = Process.Start(info)!;
        var err = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + err.Result);
    }

    private static string Tool(string name) => Path.Combine(FindRoot(), "tools", "sqlgate", name);

    private (int Code, string Output) Py(string script, params string[] args) =>
        Run("python3", ["-I", Tool(script), .. args], _dir);

    // ----- assignment and scope injection -----

    private string[] MakeFiles(int count)
    {
        var names = new List<string>();
        for (int i = 0; i < count; i++)
        {
            string rel = $"src/TrinoSqlEngine/Ast/Emit/F{i:D2}.cs";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_dir, rel))!);
            File.WriteAllText(Path.Combine(_dir, rel), string.Join('\n', Enumerable.Repeat("x;", 5 + (i * 37 % 90))) + "\n");
            names.Add(rel);
        }

        File.WriteAllLines(Path.Combine(_dir, "all.txt"), names);
        return [.. names];
    }

    private string[] ShardFiles(int index, int shards)
    {
        var r = Py("g7-shard.py", "--in-scope-list", Path.Combine(_dir, "all.txt"), "--root", _dir, "--shards", shards.ToString(), "--index", index.ToString());
        r.Code.ShouldBe(0, r.Output);
        return r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void G7Shard_Assignment_IsAPartition_Deterministic_AndBalanced()
    {
        string[] all = MakeFiles(30);

        var shards = Enumerable.Range(0, 6).Select(i => ShardFiles(i, 6)).ToArray();

        shards.SelectMany(s => s).Order().ShouldBe(all.Order());              // union is the full set, no duplicates
        shards.SelectMany(s => s).Distinct().Count().ShouldBe(all.Length);
        Enumerable.Range(0, 6).Select(i => ShardFiles(i, 6)).ShouldBe(shards); // deterministic
        var loads = shards.Select(s => s.Sum(f => File.ReadAllLines(Path.Combine(_dir, f)).Length)).ToArray();
        loads.Max().ShouldBeLessThan((int)(loads.Min() * 1.3));
    }

    [Fact]
    public void G7Shard_MoreShardsThanFiles_Fails()
    {
        MakeFiles(3);

        var r = Py("g7-shard.py", "--in-scope-list", Path.Combine(_dir, "all.txt"), "--root", _dir, "--shards", "4", "--index", "0");

        r.Code.ShouldBe(1, r.Output);
    }

    [Fact]
    public void G7Shard_ScopeApply_KeepsOnlyTheShardInScope_AndShardsCoverTheGlobSet()
    {
        Run("git", ["init", "-q"], _dir).Code.ShouldBe(0);
        File.WriteAllText(Path.Combine(_dir, "stryker-config.json"),
            JsonSerializer.Serialize(new Dictionary<string, object> { ["stryker-config"] = new { mutate = new[] { "**/Ast/Emit/*.cs" } } }));
        string[] all = MakeFiles(6);
        File.WriteAllText(Path.Combine(_dir, "src/TrinoSqlEngine/Other.cs"), "class O { }\n");
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "all.txt\n");
        Run("git", ["add", "-A"], _dir).Code.ShouldBe(0);
        Run("git", ["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "f"], _dir).Code.ShouldBe(0);

        var listed = Enumerable.Range(0, 3)
            .SelectMany(i => Run("bash", [Tool("g7-scope.sh"), "--list", "--shard", $"{i}/3", "--root", _dir], _dir).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        listed.Order().ShouldBe(all.Order());

        var apply = Run("bash", [Tool("g7-scope.sh"), "--apply", "--shard", "1/3", "--root", _dir], _dir);
        apply.Code.ShouldBe(0, apply.Output);
        string[] mine = Run("bash", [Tool("g7-scope.sh"), "--list", "--shard", "1/3", "--root", _dir], _dir).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (string f in all)
        {
            bool marked = File.ReadAllText(Path.Combine(_dir, f)).Contains("Stryker disable all", StringComparison.Ordinal);
            marked.ShouldBe(!mine.Contains(f), f);
        }

        File.ReadAllText(Path.Combine(_dir, "src/TrinoSqlEngine/Other.cs")).ShouldContain("Stryker disable all");
    }

    // ----- aggregate (T-09) -----

    private static readonly string[] Full =
    [
        "src/TrinoSqlEngine/Ast/Emit/A.cs", "src/TrinoSqlEngine/Ast/Emit/B.cs", "src/TrinoSqlEngine/Ast/Security/C.cs",
    ];

    // Shard 0 holds A (1 killed of 1), shard 1 holds B and C (2 killed of 10): the mean of the shard scores (60%) differs from the sum (27.3%).
    private static Dictionary<string, string[]> Statuses() => new()
    {
        [Full[0]] = ["Killed"],
        [Full[1]] = [.. Enumerable.Repeat("Killed", 2), .. Enumerable.Repeat("Survived", 4)],
        [Full[2]] = ["Survived", "NoCoverage", "Ignored", .. Enumerable.Repeat("Survived", 2)],
    };

    private static string ReportJson(Dictionary<string, string[]> files, string prefix = "/w/") =>
        JsonSerializer.Serialize(new
        {
            files = files.ToDictionary(kv => prefix + kv.Key, kv => new { mutants = kv.Value.Select(s => new { status = s }).ToArray() }),
        });

    private string Reduce(int shard, int shards, Dictionary<string, string[]> report, string[] shardFiles, bool writeReport = true)
    {
        string dir = Path.Combine(_dir, $"s{shard}");
        Directory.CreateDirectory(Path.Combine(dir, "reports"));
        // Like Stryker with the scope injection: the mutants of the files of other shards are Ignored.
        var forShard = report.ToDictionary(kv => kv.Key, kv => Full.Contains(kv.Key) && !shardFiles.Contains(kv.Key) ? kv.Value.Select(_ => "Ignored").ToArray() : kv.Value);
        if (writeReport) File.WriteAllText(Path.Combine(dir, "reports", "mutation-report.json"), ReportJson(forShard));
        string list = Path.Combine(dir, "list.txt");
        File.WriteAllLines(list, shardFiles);
        string outFile = Path.Combine(_dir, "results", $"g7-shard-{shard}.json");
        var r = Py("g7-aggregate.py", "reduce", "--report-glob", Path.Combine(dir, "reports", "mutation-report.json"),
            "--in-scope-list", list, "--shard", shard.ToString(), "--shards", shards.ToString(), "--out", outFile);
        return r.Code == 0 ? outFile : r.Output;
    }

    private (int Code, string Output) Merge(int shards, string[] full, int floor = 1, int breakAt = 85, string needs = "success")
    {
        string list = Path.Combine(_dir, "full.txt");
        File.WriteAllLines(list, full);
        return Py("g7-aggregate.py", "merge", "--results-glob", Path.Combine(_dir, "results", "g7-shard-*.json"), "--in-scope-list", list,
            "--shards", shards.ToString(), "--floor", floor.ToString(), "--break-at", breakAt.ToString(), "--needs-result", needs);
    }

    private void TwoShards(Dictionary<string, string[]>? statuses = null)
    {
        var s = statuses ?? Statuses();
        File.Exists(Reduce(0, 2, s, [Full[0]])).ShouldBeTrue();
        File.Exists(Reduce(1, 2, s, [Full[1], Full[2]])).ShouldBeTrue();
    }

    // WAVE3-SQLGATE-T-09
    [Fact]
    public void Aggregate_SumsRawCounts_NotShardScores()
    {
        TwoShards();

        var merged = Merge(2, Full, floor: 1, breakAt: 0);
        merged.Code.ShouldBe(0, merged.Output);

        // The single-run formula (g7-summary.py on the combined report) gives the same score line.
        string combined = Path.Combine(_dir, "single", "reports", "mutation-report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(combined)!);
        File.WriteAllText(combined, ReportJson(Statuses()));
        string list = Path.Combine(_dir, "full.txt");
        var single = Py("g7-summary.py", "--report-glob", combined, "--in-scope-list", list, "--floor", "1");
        single.Code.ShouldBe(0, single.Output);

        ScoreLine().Match(merged.Output).Value.ShouldBe(ScoreLine().Match(single.Output).Value);
        merged.Output.ShouldContain("Score: 27.27%");   // not the 60% mean of the shard scores
        Merge(2, Full, floor: 1, breakAt: 85).Code.ShouldBe(1);
        Merge(2, Full, floor: 1, breakAt: 85).Output.ShouldContain("below the break");
    }

    [Fact]
    public void Aggregate_AboveBreak_Passes()
    {
        var s = Statuses();
        s[Full[1]] = [.. Enumerable.Repeat("Killed", 40), "Survived"];
        s[Full[2]] = Enumerable.Repeat("Killed", 20).ToArray();
        TwoShards(s);

        var r = Merge(2, Full, floor: 60);

        r.Code.ShouldBe(0, r.Output);
    }

    [Fact]
    public void Aggregate_MissingShardReport_Fails()
    {
        File.Exists(Reduce(0, 2, Statuses(), [Full[0]])).ShouldBeTrue();

        var r = Merge(2, Full, breakAt: 0);

        r.Code.ShouldBe(1, r.Output);
        r.Output.ShouldContain("shard 1 has no report");
    }

    [Fact]
    public void Aggregate_ShardWithoutReport_ReduceFails()
    {
        string result = Reduce(0, 2, Statuses(), [Full[0]], writeReport: false);

        File.Exists(result).ShouldBeFalse();
    }

    [Fact]
    public void Aggregate_FileSetMismatch_Fails()
    {
        TwoShards();

        string[] bigger = [.. Full, "src/TrinoSqlEngine/Ast/Emit/D.cs"];
        var missing = Merge(2, bigger, breakAt: 0);
        var extra = Merge(2, Full[..2], breakAt: 0);

        missing.Code.ShouldBe(1, missing.Output);
        missing.Output.ShouldContain("covered by no shard");
        extra.Code.ShouldBe(1, extra.Output);
        extra.Output.ShouldContain("not in the full in-scope list");
    }

    [Fact]
    public void Aggregate_FileInTwoShards_Fails()
    {
        var s = Statuses();
        File.Exists(Reduce(0, 2, s, [Full[0], Full[1]])).ShouldBeTrue();
        File.Exists(Reduce(1, 2, s, [Full[1], Full[2]])).ShouldBeTrue();

        var r = Merge(2, Full, breakAt: 0);

        r.Code.ShouldBe(1, r.Output);
        r.Output.ShouldContain("covered by shards 0 and 1");
    }

    [Fact]
    public void Aggregate_ShardJobNotSuccess_Fails()
    {
        TwoShards();

        Merge(2, Full, breakAt: 0, needs: "cancelled").Code.ShouldBe(1);
        Merge(2, Full, breakAt: 0, needs: "failure").Output.ShouldContain("not success");
    }

    [Fact]
    public void Aggregate_WrongShardCountOrBelowFloor_Fails()
    {
        TwoShards();

        Merge(3, Full, breakAt: 0).Code.ShouldBe(1);
        var floor = Merge(2, Full, floor: 1000, breakAt: 0);
        floor.Code.ShouldBe(1);
        floor.Output.ShouldContain("below the floor");
    }

    [Fact]
    public void Aggregate_NonIgnoredOutsideTheFiles_Fails()
    {
        var s = Statuses();
        s["src/TrinoSqlEngine/Parsing/P.cs"] = ["Survived", "Ignored"];
        File.Exists(Reduce(0, 2, s, [Full[0]])).ShouldBeTrue();
        File.Exists(Reduce(1, 2, s, [Full[1], Full[2]])).ShouldBeTrue();

        var r = Merge(2, Full, breakAt: 0);

        r.Code.ShouldBe(1, r.Output);
        r.Output.ShouldContain("outside the globs");
    }
}
