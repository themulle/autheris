using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Shouldly;

namespace Autheris.Tests.Simulation.Meta;

/// <summary>Tests of <c>scripts/sim/fetch-sqllogictest.sh</c> and <c>.github/scripts/sim-report.py</c> as external processes (no network).</summary>
public sealed class SltScriptTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "sim-script-" + Guid.NewGuid().ToString("N"));

    public SltScriptTests() => Directory.CreateDirectory(_tmp);

    public void Dispose() => Directory.Delete(_tmp, true);

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
        }
    }

    private static string Script => Path.Combine(RepoRoot, "scripts", "sim", "fetch-sqllogictest.sh");

    private static string Report => Path.Combine(RepoRoot, ".github", "scripts", "sim-report.py");

    private static string RealManifest => Path.Combine(RepoRoot, "tests", "Autheris.Tests.Simulation", "Corpus", "sqllogictest.manifest.json");

    private static (int Exit, string Output) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
        {
            p.Kill(true);
            throw new TimeoutException($"{file} did not finish in 60 s");
        }

        return (p.ExitCode, output.Result + error.Result);
    }

    private string TempManifest(string fileContent, out string dir, string? shaOverride = null)
    {
        dir = Path.Combine(_tmp, "corpus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "test"));
        File.WriteAllText(Path.Combine(dir, "test", "a.test"), fileContent);
        var sha = shaOverride ?? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fileContent)));
        var manifest = Path.Combine(_tmp, "m-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(manifest, $$"""{"commit":"{{new string('a', 40)}}","host":"raw.githubusercontent.com","repo":"o/r","files":[{"path":"test/a.test","queries":1,"sha256":"{{sha}}"}]}""");
        return manifest;
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-10
    public void SltFetch_FailsClosedOnShaMismatch()
    {
        var manifest = TempManifest("statement ok\nSELECT 1\n", out var dir);
        Run("bash", Script, "--verify", manifest, dir).Exit.ShouldBe(0);
        Directory.Exists(dir).ShouldBeTrue();

        File.AppendAllText(Path.Combine(dir, "test", "a.test"), "tampered\n");
        var (exit, output) = Run("bash", Script, "--verify", manifest, dir);

        exit.ShouldNotBe(0);
        output.ShouldContain("sha256 mismatch");
        Directory.Exists(dir).ShouldBeFalse();

        var withoutHashes = TempManifest("x", out var dir2, shaOverride: "nothex");
        Run("bash", Script, "--verify", withoutHashes, dir2).Exit.ShouldNotBe(0);
        Directory.Exists(dir2).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-10
    public void SltFetch_RejectsNonHttpsOrUnpinnedHost()
    {
        var manifest = RealManifest;
        var commit = "c93ac416f05635ff5cc490009add936d9753d01d";
        var good = $"https://raw.githubusercontent.com/gregrahn/sqllogictest/{commit}/test/select1.test";

        Run("bash", Script, "--check-url", manifest, good).Exit.ShouldBe(0);
        foreach (var bad in new[]
                 {
                     good.Replace("https://", "http://", StringComparison.Ordinal),
                     good.Replace("raw.githubusercontent.com", "evil.example", StringComparison.Ordinal),
                     good.Replace("raw.githubusercontent.com", "raw.githubusercontent.com@evil.example", StringComparison.Ordinal),
                     good.Replace(commit, "master", StringComparison.Ordinal),
                     good.Replace(commit, new string('0', 40), StringComparison.Ordinal),
                     good + "?x=1",
                     good.Replace("/test/", "/../test/", StringComparison.Ordinal),
                     "file:///etc/passwd",
                 })
        {
            Run("bash", Script, "--check-url", manifest, bad).Exit.ShouldNotBe(0, bad);
        }

        // a manifest naming a host outside the script allowlist is refused as well
        var evil = Path.Combine(_tmp, "evil.json");
        File.WriteAllText(evil, File.ReadAllText(manifest).Replace("raw.githubusercontent.com", "evil.example", StringComparison.Ordinal));
        Run("bash", Script, "--check-url", evil, $"https://evil.example/gregrahn/sqllogictest/{commit}/test/select1.test").Exit.ShouldNotBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-10/11
    public void SltFetch_LeavesNoPartialDirectory()
    {
        var manifest = TempManifest("statement ok\nSELECT 1\n", out var dir);
        File.Delete(Path.Combine(dir, "test", "a.test"));
        Run("bash", Script, "--verify", manifest, dir).Exit.ShouldNotBe(0);
        Directory.Exists(dir).ShouldBeFalse();

        var empty = TempManifest("x", out var dir2);
        File.WriteAllBytes(Path.Combine(dir2, "test", "a.test"), []);
        Run("bash", Script, "--verify", empty, dir2).Exit.ShouldNotBe(0);
        Directory.Exists(dir2).ShouldBeFalse();

        // an existing output directory is never touched or overwritten
        var existing = Path.Combine(_tmp, "existing");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "keep"), "x");
        Run("bash", Script, RealManifest, existing).Exit.ShouldNotBe(0);
        File.Exists(Path.Combine(existing, "keep")).ShouldBeTrue();
        Directory.GetDirectories(_tmp, "existing.partial.*").ShouldBeEmpty();

        var (exit, output) = Run("bash", Script, "--self-test");
        exit.ShouldBe(0);
        output.ShouldContain("self-test passed");
    }

    private string Results(string text)
    {
        var path = Path.Combine(_tmp, "r-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, text);
        return path;
    }

    private string Baseline()
    {
        var path = Path.Combine(_tmp, "b-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"v":1,"corpus":{"minExecuted":5000,"maxHarnessMismatchPercent":1,"knownGapClasses":["WrongResult:known"]}}""");
        return path;
    }

    private static string Line(string kind, string cls, int count, string repro = "r", string? disposition = null) =>
        $$"""{"v":1,"suite":"corpus","kind":"{{kind}}","errorClass":"{{cls}}","count":{{count}},"firstId":"abc","repro":"{{repro}}"{{(disposition is null ? "" : $",\"disposition\":\"{disposition}\"")}}}""" + "\n";

    private static string Ok => Line("Executed", "scenario:A", 5000, "sample=5000;seed=1") + Line("Equal", "all", 4990);

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-19
    public void SimReport_FailsClosedOnEmptyMalformedOrOversizedInput()
    {
        var baseline = Baseline();
        var outMd = Path.Combine(_tmp, "o.md");
        int Exit(string results) => Run("python3", "-I", Report, results, baseline, outMd).Exit;

        Exit(Results(Ok)).ShouldBe(0);
        Exit(Results(string.Empty)).ShouldNotBe(0); // empty results are a failure, not green
        Exit(Results("\n\n")).ShouldNotBe(0);
        Exit(Results("not json\n")).ShouldNotBe(0);
        Exit(Results(Ok.TrimEnd('\n'))).ShouldNotBe(0); // truncated last line
        Exit(Results("""{"v":1,"suite":"corpus","kind":"Equal""" + "\n")).ShouldNotBe(0);
        Exit(Results(Ok.Replace("\"v\":1", "\"v\":2", StringComparison.Ordinal))).ShouldNotBe(0); // unknown schema version
        Exit(Results(Ok + Line("Bogus", "x", 1))).ShouldNotBe(0);
        Exit(Path.Combine(_tmp, "missing.jsonl")).ShouldNotBe(0);
        Exit(Results(new string('x', 10 * 1024 * 1024 + 1))).ShouldNotBe(0);
        Run("python3", "-I", Report, Results(Ok), Path.Combine(_tmp, "missing-baseline.json"), outMd).Exit.ShouldNotBe(0);

        // red outcomes: a security class, an unclassified difference, too few executed queries
        Exit(Results(Ok + Line("PolicyLeak", "PolicyLeak:x", 1, disposition: "PolicyLeak"))).ShouldNotBe(0);
        Exit(Results(Ok + Line("Crash", "Crash:x", 1))).ShouldNotBe(0);
        Exit(Results(Ok + Line("WrongResult", "WrongResult:new", 1))).ShouldNotBe(0);
        Exit(Results(Line("Executed", "scenario:A", 10, "sample=5000;seed=1"))).ShouldNotBe(0);
        Exit(Results(Ok + Line("WrongResult", "WrongResult:known", 2, disposition: "KnownGap"))).ShouldBe(0);

        var (selfExit, selfOutput) = Run("python3", "-I", Report, "--self-test");
        selfExit.ShouldBe(0);
        selfOutput.ShouldContain("self-test passed");
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-14
    public void SimReport_EscapesMarkdownAndRedactsCanaries()
    {
        var outMd = Path.Combine(_tmp, "o.md");
        var hostile = Line("WrongResult", "WrongResult:known|`<script>SIMCANARY-ABC-1", 1, repro: "SIMCANARY-XYZ <img src=x> \\u0007\\u0000 end", disposition: "KnownGap");

        var exit = Run("python3", "-I", Report, Results(Ok + hostile), Baseline(), outMd).Exit;

        exit.ShouldNotBe(0); // the class is not the baselined one
        var text = File.ReadAllText(outMd);
        text.ShouldNotContain("SIMCANARY");
        text.ShouldContain("&lt;canary&gt;");
        text.ShouldNotContain("<script>");
        text.ShouldNotContain("<img");
        text.ShouldNotContain("\u0007");
        text.ShouldNotContain("\u0000");
        text.Split('\n').Where(l => l.StartsWith("| WrongResult", StringComparison.Ordinal)).ShouldAllBe(l => !l.Contains('`', StringComparison.Ordinal));
    }
}
