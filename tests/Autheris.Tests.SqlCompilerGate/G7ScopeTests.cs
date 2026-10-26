namespace Autheris.Tests.SqlCompilerGate;

using System.Diagnostics;
using System.Text.Json;
using Shouldly;

/// <summary>
/// G7 scope injection and summary scripts (WAVE3-SQLGATE-SC-01/02/03, T-02..T-08). The scripts run as processes against fixture
/// repositories; they need bash, git, jq and python3 (all present on the CI runner).
/// </summary>
[Trait("Category", "Security")]
public sealed class G7ScopeTests : IDisposable
{
    private const string Marker = "// Stryker disable all : outside the G7 mutate scope";
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "g7-fixture-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { ForceDelete(_repo); } catch (IOException) { }
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) return dir.FullName;
        }

        throw new InvalidOperationException("Autheris.sln was not found.");
    }

    private static void ForceDelete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
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

    private (int Code, string Output) Scope(string mode, Dictionary<string, string?>? env = null) =>
        Run("bash", [Path.Combine(FindRoot(), "tools", "sqlgate", "g7-scope.sh"), mode, "--root", _repo], _repo, env);

    private void File_(string relative, string content)
    {
        string path = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Config(params string[] globs) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["stryker-config"] = new { mutate = globs } });

    private void Fixture(string config, bool commit = true)
    {
        Directory.CreateDirectory(_repo);
        Run("git", ["init", "-q"], _repo).Code.ShouldBe(0);
        File_("stryker-config.json", config);
        File_("src/TrinoSqlEngine/Ast/Emit/Emitter.cs", "namespace X;\nclass Emitter { }\n");
        File_("src/TrinoSqlEngine/Ast/Security/Guard.cs", "namespace X;\nclass Guard { }\n");
        File_("src/TrinoSqlEngine/Parsing/Parser.cs", "namespace X;\nclass Parser { }\n");
        File_("src/TrinoSqlEngine/Governance/Gov.cs", "\uFEFFnamespace X;\nclass Gov { }\n");
        File_("src/TrinoSqlEngine/Governance/Gen.g.cs", "namespace X;\nclass Gen { }\n");
        File_("src/TrinoSqlEngine/obj/Skip.cs", "class Skip { }\n");
        if (commit)
        {
            Run("git", ["add", "-A"], _repo).Code.ShouldBe(0);
            Run("git", ["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "fixture"], _repo).Code.ShouldBe(0);
        }
    }

    private static readonly string TwoGlobs = Config("**/Ast/Emit/*.cs", "**/Ast/Security/*.cs");

    [Fact]
    public void G7Scope_Apply_MarksOnlyOutOfScopeFiles()
    {
        Fixture(TwoGlobs);

        var result = Scope("--apply");

        result.Code.ShouldBe(0, result.Output);
        File.ReadLines(Path.Combine(_repo, "src/TrinoSqlEngine/Parsing/Parser.cs")).First().ShouldBe(Marker);
        byte[] bom = File.ReadAllBytes(Path.Combine(_repo, "src/TrinoSqlEngine/Governance/Gov.cs"));
        bom[..3].ShouldBe([(byte)0xEF, (byte)0xBB, (byte)0xBF]);   // the BOM stays first, the marker follows it
        System.Text.Encoding.UTF8.GetString(bom, 3, Marker.Length).ShouldBe(Marker);
        File.ReadAllText(Path.Combine(_repo, "src/TrinoSqlEngine/Governance/Gen.g.cs")).ShouldNotContain("Stryker");
        File.ReadAllText(Path.Combine(_repo, "src/TrinoSqlEngine/obj/Skip.cs")).ShouldNotContain("Stryker");
    }

    // WAVE3-SQLGATE-T-04: the marker is never added to a file that matches a glob.
    [Fact]
    public void G7Scope_InScopeFile_NeverMarked()
    {
        Fixture(TwoGlobs);

        Scope("--apply").Code.ShouldBe(0);

        File.ReadAllText(Path.Combine(_repo, "src/TrinoSqlEngine/Ast/Emit/Emitter.cs")).ShouldNotContain("Stryker");
        File.ReadAllText(Path.Combine(_repo, "src/TrinoSqlEngine/Ast/Security/Guard.cs")).ShouldNotContain("Stryker");
    }

    [Fact]
    public void G7Scope_InScopeFileAlreadyMarked_Fails()
    {
        Fixture(TwoGlobs, commit: false);
        File_("src/TrinoSqlEngine/Ast/Emit/Emitter.cs", "// Stryker disable all\nclass E { }\n");
        Run("git", ["add", "-A"], _repo);
        Run("git", ["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "x"], _repo);

        var result = Scope("--apply");

        result.Code.ShouldBe(1, result.Output);
        File.ReadAllText(Path.Combine(_repo, "src/TrinoSqlEngine/Parsing/Parser.cs")).ShouldNotContain("Stryker");
    }

    [Fact]
    public void G7Scope_EmptyScope_Fails()
    {
        Fixture(Config());

        Scope("--apply").Code.ShouldBe(1);
        Scope("--list").Code.ShouldBe(1);
    }

    [Fact]
    public void G7Scope_GlobMatchingNoFile_Fails()
    {
        Fixture(Config("**/Ast/Emit/*.cs", "**/Ast/Nothing/*.cs"));

        var result = Scope("--apply");

        result.Code.ShouldBe(1, result.Output);
        result.Output.ShouldContain("matches no file");
    }

    [Fact]
    public void G7Scope_DirtyTree_Fails()
    {
        Fixture(TwoGlobs);
        File_("src/TrinoSqlEngine/Parsing/Parser.cs", "namespace X;\nclass Parser2 { }\n");

        var result = Scope("--apply");

        result.Code.ShouldBe(1, result.Output);
        File.ReadAllText(Path.Combine(_repo, "src/TrinoSqlEngine/Governance/Gov.cs")).ShouldNotContain("Stryker");
    }

    [Fact]
    public void G7Scope_OutsideCi_Fails()
    {
        Fixture(TwoGlobs);

        Scope("--apply", new() { ["CI"] = null }).Code.ShouldBe(1);
    }

    [Fact]
    public void G7Scope_List_PrintsInScopeFilesOnly()
    {
        Fixture(TwoGlobs);

        var result = Scope("--list");

        result.Code.ShouldBe(0, result.Output);
        result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order().ShouldBe(
            ["src/TrinoSqlEngine/Ast/Emit/Emitter.cs", "src/TrinoSqlEngine/Ast/Security/Guard.cs"]);
    }

    // ----- g7-summary.py -----

    private static string Mutant(string status) => "{\"status\":\"" + status + "\"}";

    private static string ReportJson(Dictionary<string, string[]> files) =>
        JsonSerializer.Serialize(new { files = files.ToDictionary(kv => kv.Key, kv => new { mutants = kv.Value.Select(s => JsonDocument.Parse(Mutant(s)).RootElement).ToArray() }) });

    private (int Code, string Output) Summary(string? report, string[] inScope, int floor)
    {
        Directory.CreateDirectory(_repo);
        string list = Path.Combine(_repo, "inscope.txt");
        File.WriteAllLines(list, inScope);
        if (report is not null)
        {
            string dir = Path.Combine(_repo, "StrykerOutput", "2026", "reports");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "mutation-report.json"), report);
        }

        File.WriteAllText(Path.Combine(_repo, "stryker-log.txt"), "[10:00:00 INF] progress line\n");
        return Run("python3", ["-I", Path.Combine(FindRoot(), "tools", "sqlgate", "g7-summary.py"),
            "--report-glob", Path.Combine(_repo, "StrykerOutput", "*", "reports", "mutation-report.json"),
            "--in-scope-list", list, "--floor", floor.ToString(), "--log-glob", Path.Combine(_repo, "stryker-log.txt")], _repo);
    }

    private static readonly string[] Scoped = ["src/TrinoSqlEngine/Ast/Emit/E.cs", "src/TrinoSqlEngine/Ast/Security/S.cs"];

    private static Dictionary<string, string[]> HealthyFiles() => new()
    {
        ["/w/src/TrinoSqlEngine/Ast/Emit/E.cs"] = ["Killed", "Killed", "Survived", "Ignored"],
        ["/w/src/TrinoSqlEngine/Ast/Security/S.cs"] = ["Killed", "Timeout", "NoCoverage"],
        ["/w/src/TrinoSqlEngine/Parsing/P.cs"] = ["Ignored", "Ignored"],
    };

    [Fact]
    public void G7Summary_HealthyReport_Passes()
    {
        var result = Summary(ReportJson(HealthyFiles()), Scoped, floor: 5);

        result.Code.ShouldBe(0, result.Output);
        result.Output.ShouldContain("Score:");
    }

    [Fact]
    public void G7Summary_ReportMissing_Fails()
    {
        var result = Summary(null, Scoped, floor: 5);

        result.Code.ShouldBe(1);
        result.Output.ShouldContain("timeout or cancel");
        result.Output.ShouldContain("progress line");
    }

    [Fact]
    public void G7Summary_ReportUnparseable_Fails()
    {
        Summary("{not json", Scoped, floor: 5).Code.ShouldBe(1);
    }

    [Fact]
    public void G7Summary_InScopeFileWithZeroMutants_Fails()
    {
        var files = HealthyFiles();
        files["/w/src/TrinoSqlEngine/Ast/Security/S.cs"] = ["Ignored", "CompileError"];

        var result = Summary(ReportJson(files), Scoped, floor: 1);

        result.Code.ShouldBe(1);
        result.Output.ShouldContain("S.cs has zero valid mutants");
    }

    [Fact]
    public void G7Summary_InScopeFileAbsentFromReport_Fails()
    {
        var files = HealthyFiles();
        files.Remove("/w/src/TrinoSqlEngine/Ast/Security/S.cs");

        Summary(ReportJson(files), Scoped, floor: 1).Code.ShouldBe(1);
    }

    [Fact]
    public void G7Summary_BelowFloor_Fails()
    {
        var result = Summary(ReportJson(HealthyFiles()), Scoped, floor: 7);   // 6 valid mutants

        result.Code.ShouldBe(1);
        result.Output.ShouldContain("below the floor");
    }

    [Fact]
    public void G7Summary_NonIgnoredOutsideGlobs_Fails()
    {
        var files = HealthyFiles();
        files["/w/src/TrinoSqlEngine/Parsing/P.cs"] = ["Ignored", "Survived"];

        var result = Summary(ReportJson(files), Scoped, floor: 5);

        result.Code.ShouldBe(1);
        result.Output.ShouldContain("outside the globs");
    }
}
