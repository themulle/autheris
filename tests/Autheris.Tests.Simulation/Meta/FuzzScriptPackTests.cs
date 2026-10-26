using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Autheris.Tests.Simulation.Fuzz;
using Shouldly;

namespace Autheris.Tests.Simulation.Meta;

/// <summary>WP3 weekly fix (WP-PF1, SEC-PF-01..06): findings pack, failure summary and memory gates of scripts/sim/run-parser-fuzz.sh without AFL.</summary>
public sealed class FuzzScriptPackTests
{
    private const string Crash1 = "id:000000,sig:02,src:002470,time:2014286,execs:4925430,op:havoc,rep:2";
    private const string Crash2 = "id:000001,sig:02,src:002471,time:2114286,execs:5025430,op:havoc,rep:1";
    private const string Hang1 = "id:000000,src:000003,time:99,execs:12,op:havoc,rep:1";

    [Fact]
    [Trait("Category", "Security")]
    // SEC-PF-01
    public void Pack_ReportDirOutsideAllowedRoots_Refused()
    {
        using var env = new Env();
        var outside = FuzzTestPaths.NewTempDir();
        try
        {
            env.Crash(Crash1, "x");
            var result = env.Summarize(1, report: outside);
            result.ExitCode.ShouldBe(1);
            result.Output.ShouldContain("refusing report dir");
            Directory.GetFileSystemEntries(outside).ShouldBeEmpty();
            env.Run(["--check-paths"], report: outside).ExitCode.ShouldBe(1);
            env.Run(["--check-paths"], report: Path.Combine(env.Temp, ".git", "r")).ExitCode.ShouldBe(1);
            env.Run(["--check-paths"], report: env.Temp + "/../x").ExitCode.ShouldBe(1);
            env.Run(["--check-paths"], report: "relative").ExitCode.ShouldBe(1);
            env.Run(["--check-paths"]).Output.ShouldContain("report-dir=" + env.Report);
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-PF-01
    public void Pack_ReportDirSymlinkEscape_Refused()
    {
        using var env = new Env();
        var outside = FuzzTestPaths.NewTempDir();
        try
        {
            env.Crash(Crash1, "x");
            var link = Path.Combine(env.Temp, "link");
            Directory.CreateSymbolicLink(link, outside);
            env.Summarize(1, report: Path.Combine(link, "sub")).ExitCode.ShouldBe(1);
            Directory.GetFileSystemEntries(outside).ShouldBeEmpty();

            // A pre-existing destination symlink is replaced, never written through.
            var report = Path.Combine(env.Temp, "rep");
            Directory.CreateDirectory(report);
            var victim = Path.Combine(outside, "victim");
            File.WriteAllText(victim, "keep");
            File.CreateSymbolicLink(Path.Combine(report, "afl-findings.tar.gz"), victim);
            env.Summarize(1, report: report).ExitCode.ShouldBe(1);
            File.ReadAllText(victim).ShouldBe("keep");
            new FileInfo(Path.Combine(report, "afl-findings.tar.gz")).LinkTarget.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-PF-02
    public void Pack_SymlinkInFindings_NotFollowedOrSkipped()
    {
        using var env = new Env();
        var outside = FuzzTestPaths.NewTempDir();
        try
        {
            var secret = Path.Combine(outside, "secret");
            File.WriteAllText(secret, "TOP-SECRET-CONTENT");
            env.Crash(Crash1, "real");
            File.CreateSymbolicLink(Path.Combine(env.Findings("crashes"), Crash2), secret);

            env.Summarize(1).ExitCode.ShouldBe(1);
            var names = env.TarNames();
            names.ShouldBe([$"crashes/{Crash1}"]);
            env.TarText().ShouldNotContain("TOP-SECRET-CONTENT");
            File.ReadAllText(env.ReportFile("afl-findings.manifest")).ShouldNotContain("TOP-SECRET-CONTENT");
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-PF-02
    public void Pack_OverBound_Fails()
    {
        using var env = new Env();
        for (var i = 0; i < 2001; i++)
        {
            File.WriteAllText(Path.Combine(env.Findings("crashes"), $"id:{i:D6},sig:02,time:1,execs:1"), "x");
        }

        var result = env.Summarize(1);
        result.ExitCode.ShouldBe(1);
        result.Output.ShouldContain("over bound: 2001 files");
        File.Exists(env.ReportFile("afl-findings.tar.gz")).ShouldBeFalse();

        var big = new Env();
        using (big)
        {
            using var fs = File.Create(Path.Combine(big.Findings("hangs"), Hang1));
            fs.SetLength(64L * 1024 * 1024 + 1);
            fs.Dispose();
            big.Summarize(1).Output.ShouldContain("over bound");
            File.Exists(big.ReportFile("afl-findings.tar.gz")).ShouldBeFalse();
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-PF-02
    public void Pack_TarHasOnlyRelativeRegularFiles()
    {
        using var env = new Env();
        env.Crash(Crash1, "a");
        env.Crash(Crash2, "bb");
        env.Hang(Hang1, "ccc");
        File.WriteAllText(Path.Combine(env.Findings("crashes"), "README.txt"), "readme");
        File.WriteAllText(Path.Combine(env.AflDefault, ".state"), "state");
        Directory.CreateDirectory(Path.Combine(env.AflDefault, "queue"));
        File.WriteAllText(Path.Combine(env.AflDefault, "queue", "id:000000"), "q");

        env.Summarize(1).ExitCode.ShouldBe(1);
        var entries = env.TarEntries();
        entries.Select(e => e.Name).Order(StringComparer.Ordinal).ShouldBe([$"crashes/{Crash1}", $"crashes/{Crash2}", $"hangs/{Hang1}"]);
        entries.ShouldAllBe(e => (e.EntryType == TarEntryType.RegularFile || e.EntryType == TarEntryType.V7RegularFile) && e.Uid == 0 && e.Gid == 0 && !e.Name.StartsWith('/') && !e.Name.Contains(".."));
        Path.GetFileName(env.ReportFile("afl-findings.tar.gz")).ShouldNotContain(":");

        var manifest = File.ReadAllLines(env.ReportFile("afl-findings.manifest"));
        manifest.Length.ShouldBe(3);
        var expected = Convert.ToHexStringLower(SHA256.HashData("bb"u8));
        manifest.ShouldContain($"{expected} 2 {Crash2}");
        manifest.ShouldAllBe(l => Regex.IsMatch(l, "^[0-9a-f]{64} [0-9]+ [A-Za-z0-9._:,+=-]+$"));
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-PF-05
    public void Summary_HostileFileName_NoWorkflowCommandInjection()
    {
        using var env = new Env();
        env.Crash("id:000000,::error::pwned,time:1,execs:1", "x");
        env.Crash("id:000001,sig:02 ::set-output name=a::b,time:2,execs:2", "y");
        var result = env.Summarize(1);
        result.ExitCode.ShouldBe(1);
        result.Output.ShouldNotContain("pwned");
        result.Output.ShouldNotContain("set-output");
        result.Output.Split('\n').Count(l => l.StartsWith("::", StringComparison.Ordinal)).ShouldBe(1, "only the one static ::error:: line");
        result.Output.ShouldContain("first crash unsafe-name-");
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-PF-05
    public void Summary_NoRawInputBytesEchoed()
    {
        using var env = new Env();
        env.Crash(Crash1, "x");
        var replay = Path.Combine(env.Temp, "replay.log");
        File.WriteAllText(
            replay,
            "NotReproduced /x/id:000000 sha256=abc System.Exception SELECT-SECRET-INPUT\n" +
            "NotReproduced /x/id:000001 sha256=abd none\n" +
            "Crash /x/id:000002 sha256=abe Foo ::error::INJECT\n" +
            "::error::INJECT-FIRST-TOKEN rest\n" +
            "replayed 3 inputs, 3 failures\n");
        var result = env.Summarize(1, replayFile: replay);
        result.Output.ShouldNotContain("SECRET-INPUT");
        result.Output.ShouldNotContain("INJECT");
        result.Output.ShouldContain("NotReproduced=2");
        result.Output.ShouldContain("Crash=1");
        result.Output.ShouldContain("Other=1");
        result.Output.ShouldContain("saved_crashes 1, saved_hangs 0");
        result.Output.ShouldContain("first crash " + Crash1 + " at 2014286 ms after 4925430 execs");
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-PF-03
    public void Script_MemoryGatesUnchanged_FiniteMAndGcLimit()
    {
        var script = File.ReadAllText(FuzzTestPaths.Script);
        var code = script.Split('\n').Where(l => !l.TrimStart().StartsWith('#')).ToList();
        var afl = code.FindIndex(l => Regex.IsMatch(l, @"^\s*afl-fuzz\s"));
        afl.ShouldBeGreaterThan(0);
        code[afl].ShouldContain("-m \"$SIM_AFL_MEM_MB\"");
        script.ShouldNotContain("-m none");
        code.ShouldNotContain(l => Regex.IsMatch(l, @"ulimit\s+-v\s+unlimited"));
        code.Count(l => l.Contains("DOTNET_GCHeapHardLimit=0x20000000", StringComparison.Ordinal)).ShouldBe(1);
        var arena = code.FindIndex(l => l.Contains("MALLOC_ARENA_MAX=2", StringComparison.Ordinal));
        var region = code.FindIndex(l => l.Contains("DOTNET_GCRegionRange=0x40000000", StringComparison.Ordinal));
        arena.ShouldBeGreaterThan(0);
        region.ShouldBeGreaterThan(0);
        arena.ShouldBeLessThan(afl);
        region.ShouldBeLessThan(afl);
        code.Count(l => l.Contains("MALLOC_ARENA_MAX", StringComparison.Ordinal)).ShouldBe(1);
        script.ShouldContain("MEM_REGEX='^[0-9]{4}$'");
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-PF-04
    public void Sampler_BoundedAndReadsOnlyProcStatus()
    {
        var script = File.ReadAllText(FuzzTestPaths.Script);
        var code = string.Join('\n', script.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
        code.ShouldContain("sampler_loop \"$MODE\" &");
        code.ShouldContain("trap stop_sampler EXIT");
        code.ShouldContain("wait \"$SAMPLER_PID\"");
        code.ShouldContain("deadline=$((SECONDS + 10#$1 + 60))");
        code.ShouldContain("pgrep -x dotnet");
        Regex.Count(code, "/proc/").ShouldBe(1);
        code.ShouldContain("\"/proc/$pid/status\"");
        code.ShouldNotContain("/proc/$pid/cmdline");
        code.ShouldNotContain("/proc/$pid/environ");
        code.ShouldNotContain("kill -9");
        code.ShouldContain("SAMPLER_DIR=\"$RUNNER_TEMP/fuzz-sampler\"");
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-PF-06
    public void Summarize_ReplayFailure_ExitStaysNonZero()
    {
        using var env = new Env();
        env.Crash(Crash1, "x");
        env.Summarize(1).ExitCode.ShouldBe(1);
        env.Summarize(7).ExitCode.ShouldBe(7);
        var failed = env.Summarize(1);
        failed.Output.Split('\n').Count(l => l.StartsWith("::error::", StringComparison.Ordinal)).ShouldBe(1);
        failed.Output.ShouldContain("target address space peak 0 MB of limit 4096 MB");

        // A clean replay with no findings stays green and packs nothing.
        using var clean = new Env();
        var ok = clean.Summarize(0);
        ok.ExitCode.ShouldBe(0);
        File.Exists(clean.ReportFile("afl-findings.tar.gz")).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-PF-06
    public void Pack_FailureOnCleanRun_ExitsNonZero()
    {
        using var env = new Env();
        env.Crash(Crash1, "x");
        Directory.CreateDirectory(Path.Combine(env.Report, "afl-findings.tar.gz", "blocker"));
        var result = env.Summarize(0);
        result.ExitCode.ShouldBe(1);
        result.Output.ShouldContain("packing the findings failed");

        using var big = new Env();
        for (var i = 0; i < 2001; i++)
        {
            File.WriteAllText(Path.Combine(big.Findings("crashes"), $"id:{i:D6},time:1,execs:1"), "x");
        }

        big.Summarize(0).ExitCode.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-PF-06
    public void Workflow_UploadsContainNoAflPath()
    {
        var workflow = File.ReadAllText(Path.Combine(FuzzTestPaths.RepoRoot, ".github", "workflows", "simulation-nightly.yml"));
        workflow.ShouldNotContain("afl/default");
        foreach (var name in new[] { "sim-report-parser", "sim-report-parser-weekly" })
        {
            var at = workflow.IndexOf($"name: {name}\n", StringComparison.Ordinal);
            at.ShouldBeGreaterThan(0);
            var step = workflow[(at - 400)..Math.Min(workflow.Length, at + 500)];
            step.ShouldContain("if: always()");
            step.ShouldContain("retention-days: 7");
            step.ShouldContain("if-no-files-found: ignore");
        }
    }

    private sealed class Env : IDisposable
    {
        public Env()
        {
            Temp = FuzzTestPaths.NewTempDir();
            Directory.CreateDirectory(Path.Combine(Temp, "afl", "default", "crashes"));
            Directory.CreateDirectory(Path.Combine(Temp, "afl", "default", "hangs"));
            Report = Path.Combine(Temp, "report");
        }

        public string Temp { get; }

        public string Report { get; }

        public string AflDefault => Path.Combine(Temp, "afl", "default");

        public string Findings(string kind) => Path.Combine(AflDefault, kind);

        public string ReportFile(string name) => Path.Combine(Report, name);

        public void Crash(string name, string content) => File.WriteAllText(Path.Combine(Findings("crashes"), name), content);

        public void Hang(string name, string content) => File.WriteAllText(Path.Combine(Findings("hangs"), name), content);

        public (int ExitCode, string Output) Summarize(int rc, string? report = null, string? replayFile = null)
        {
            var args = new List<string> { "--summarize", Path.Combine(Temp, "afl"), rc.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (replayFile is not null)
            {
                args.Add(replayFile);
            }

            return Run(args, report);
        }

        public (int ExitCode, string Output) Run(IReadOnlyList<string> args, string? report = null)
        {
            var info = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = FuzzTestPaths.RepoRoot };
            info.ArgumentList.Add(FuzzTestPaths.Script);
            foreach (var a in args)
            {
                info.ArgumentList.Add(a);
            }

            foreach (var v in new[] { "SIM_AFL_MEM_MB", "AUTHERIS_SIM_SEED", "GITHUB_WORKSPACE" })
            {
                info.Environment.Remove(v);
            }

            info.Environment["RUNNER_TEMP"] = Temp;
            info.Environment["AUTHERIS_SIM_REPORT_DIR"] = report ?? Report;
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, stdout.Result + stderr.Result);
        }

        public List<TarEntry> TarEntries()
        {
            using var gz = new GZipStream(File.OpenRead(ReportFile("afl-findings.tar.gz")), CompressionMode.Decompress);
            using var reader = new TarReader(gz);
            var list = new List<TarEntry>();
            while (reader.GetNextEntry(copyData: true) is { } e)
            {
                list.Add(e);
            }

            return list;
        }

        public List<string> TarNames() => TarEntries().Select(e => e.Name).ToList();

        public string TarText() => string.Concat(TarEntries().Select(e =>
        {
            using var ms = new MemoryStream();
            e.DataStream?.CopyTo(ms);
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }));

        public void Dispose() => Directory.Delete(Temp, true);
    }
}
