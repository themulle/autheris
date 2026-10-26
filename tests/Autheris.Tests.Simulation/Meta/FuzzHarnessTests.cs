using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Antlr4.Runtime.Misc;
using Autheris.Tests.Fuzz.Replay;
using Autheris.Tests.Fuzz.Target;
using Autheris.Tests.Simulation.Fuzz;
using Autheris.Tests.Simulation.Infrastructure;
using Shouldly;
using TrinoSqlEngine;

#pragma warning disable CA2201 // the tests plant runtime-reserved exception types that the classifier must treat as crashes

namespace Autheris.Tests.Simulation.Meta;

/// <summary>REQ-SIMTEST P3 (WP3): the parser fuzz harness, the deterministic replay and the safety rules around it.</summary>
public sealed partial class FuzzHarnessTests
{
    // ---------- classifier ----------

    [Fact]
    public void Classifier_ParseCanceled_IsAccepted() =>
        FuzzOutcomeClassifier.Classify(new ParseCanceledException("syntax error"), TimeSpan.FromMilliseconds(5)).ShouldBe(FuzzOutcome.Accepted);

    [Fact]
    public void Classifier_ParseCanceledWithTimeoutInner_IsHang() =>
        FuzzOutcomeClassifier.Classify(new ParseCanceledException("budget", new TimeoutException()), TimeSpan.FromMilliseconds(5)).ShouldBe(FuzzOutcome.Hang);

    [Fact]
    public void Classifier_NullReference_IsCrash()
    {
        FuzzOutcomeClassifier.Classify(new NullReferenceException(), TimeSpan.Zero).ShouldBe(FuzzOutcome.Crash);
        FuzzOutcomeClassifier.Classify(new IndexOutOfRangeException(), TimeSpan.Zero).ShouldBe(FuzzOutcome.Crash);
        FuzzOutcomeClassifier.Classify(new InvalidCastException(), TimeSpan.Zero).ShouldBe(FuzzOutcome.Crash);
        FuzzOutcomeClassifier.Classify(new InsufficientExecutionStackException(), TimeSpan.Zero).ShouldBe(FuzzOutcome.Crash);
        FuzzOutcomeClassifier.Classify(new OverflowException(), TimeSpan.Zero).ShouldBe(FuzzOutcome.Crash);

        // A plain SecurityException thrown outside TrinoSqlEngine is not a documented rejection.
        FuzzOutcomeClassifier.Classify(new SecurityException("not from the engine"), TimeSpan.Zero).ShouldBe(FuzzOutcome.Crash);
        FuzzOutcomeClassifier.Classify(new ArgumentOutOfRangeException("other"), TimeSpan.Zero).ShouldBe(FuzzOutcome.Crash);
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-20
    public void Classifier_OverTimeLimit_IsHang()
    {
        FuzzOutcomeClassifier.HangLimit.ShouldBe(TimeSpan.FromSeconds(2));
        FuzzOutcomeClassifier.Classify(null, TimeSpan.FromSeconds(2.1)).ShouldBe(FuzzOutcome.Hang);
        FuzzOutcomeClassifier.Classify(new ParseCanceledException("x"), TimeSpan.FromSeconds(2.1)).ShouldBe(FuzzOutcome.Hang);
        FuzzOutcomeClassifier.Classify(null, TimeSpan.FromSeconds(2)).ShouldBe(FuzzOutcome.Accepted);
        FuzzOutcomeClassifier.Classify(new OperationCanceledException(), TimeSpan.FromSeconds(1)).ShouldBe(FuzzOutcome.Hang);
    }

    [Fact]
    public void Classifier_EngineRejectionsAreAccepted()
    {
        FuzzOutcomeClassifier.Classify(new ArgumentOutOfRangeException("sql", "too long"), TimeSpan.Zero).ShouldBe(FuzzOutcome.Accepted);
        FuzzOutcomeClassifier.Classify(new TrinoSqlEngine.Ast.Builder.SetOperationDegreeMismatchException(), TimeSpan.Zero).ShouldBe(FuzzOutcome.Accepted);
        FuzzOutcomeClassifier.Classify(new LiteralTokenRejectedException(), TimeSpan.Zero).ShouldBe(FuzzOutcome.Accepted);
        FuzzOutcomeClassifier.Classify(new UnfilteredDmlException(), TimeSpan.Zero).ShouldBe(FuzzOutcome.Accepted);
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-20
    public void AcceptedRejections_ContainOnlyTrinoSqlEngineOrDocumentedTypes()
    {
        var engine = typeof(FastSqlEngine).Assembly;
        Type[] documentedExternal = [typeof(ParseCanceledException), typeof(ArgumentOutOfRangeException), typeof(SecurityException)];

        AcceptedRejections.Types.Count.ShouldBeGreaterThan(0);
        foreach (var type in AcceptedRejections.Types)
        {
            (type.Assembly == engine || documentedExternal.Contains(type)).ShouldBeTrue($"{type.FullName} is neither a TrinoSqlEngine type nor documented");
        }

        // Broad catch types are never accepted wholesale.
        AcceptedRejections.Types.ShouldNotContain(typeof(Exception));
        AcceptedRejections.Types.ShouldNotContain(typeof(InvalidOperationException));
        AcceptedRejections.Types.ShouldNotContain(typeof(NullReferenceException));
    }

    // ---------- replay ----------

    [Fact]
    public void Replay_SeedCorpus_AllAccepted()
    {
        var report = ReplayRunner.Run([FuzzTestPaths.Corpus], new HashSet<string>());

        report.Entries.Count.ShouldBeGreaterThanOrEqualTo(250, "the seed corpus must not be empty (vacuous replay)");
        report.Failures.Select(f => $"{f.Status} {Path.GetFileName(f.Path)} {f.ExceptionType}").ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-17
    public void Replay_CommittedFindings_AllAccepted()
    {
        Directory.Exists(FuzzTestPaths.Findings).ShouldBeTrue();
        Directory.Exists(Path.Combine(FuzzTestPaths.Findings, "crashes")).ShouldBeTrue();
        Directory.Exists(Path.Combine(FuzzTestPaths.Findings, "hangs")).ShouldBeTrue();

        var quarantine = FuzzQuarantine.Load(Path.Combine(FuzzTestPaths.Findings, "quarantine.json"), DateOnly.FromDateTime(DateTime.UtcNow));
        var report = ReplayRunner.Run([FuzzTestPaths.Findings], quarantine);

        // A quarantined input still runs and is listed; every other committed input must be accepted.
        report.Failures.Select(f => $"{f.Status} {f.Path} {f.ExceptionType}").ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-17
    public void Replay_CrashAndHang_FailUnlessQuarantined_AndQuarantinedStillRuns()
    {
        var dir = FuzzTestPaths.NewTempDir();
        try
        {
            var crashInput = "crash-me"u8.ToArray();
            File.WriteAllBytes(Path.Combine(dir, "a-crash.sql"), crashInput);
            File.WriteAllBytes(Path.Combine(dir, "b-ok.sql"), "ok"u8.ToArray());
            var executed = new List<string>();

            void Target(byte[] input)
            {
                lock (executed)
                {
                    executed.Add(System.Text.Encoding.UTF8.GetString(input));
                }

                if (input.AsSpan().SequenceEqual(crashInput))
                {
                    throw new NullReferenceException();
                }
            }

            var plain = ReplayRunner.Run([dir], new HashSet<string>(), target: Target);
            plain.ExitCode.ShouldBe(1);
            plain.Failures.Single().Status.ShouldBe(ReplayStatus.Crash);
            plain.Failures.Single().ExceptionType.ShouldBe("System.NullReferenceException");
            plain.Entries.Select(e => Path.GetFileName(e.Path)).ShouldBe(["a-crash.sql", "b-ok.sql"], "input order is sorted by file name");

            executed.Clear();
            var sha = Convert.ToHexStringLower(SHA256.HashData(crashInput));
            var quarantined = ReplayRunner.Run([dir], new HashSet<string> { sha }, target: Target);
            quarantined.ExitCode.ShouldBe(0);
            quarantined.Entries[0].Status.ShouldBe(ReplayStatus.Crash);
            quarantined.Entries[0].Quarantined.ShouldBeTrue();
            executed.ShouldContain("crash-me", "a quarantined input still runs in replay");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-20
    public void Replay_Hang_IsReportedAfterTwoSeconds()
    {
        var dir = FuzzTestPaths.NewTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "hang.sql"), "x"u8.ToArray());
            var clock = Stopwatch.StartNew();

            var report = ReplayRunner.Run([dir], target: _ => Thread.Sleep(TimeSpan.FromSeconds(6)));

            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "the Join limit abandons the thread after 2 s");
            report.Entries.Single().Status.ShouldBe(ReplayStatus.Hang);
            report.ExitCode.ShouldBe(1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-17
    public void Replay_AflFinding_FailsEvenWhenItReplaysAccepted()
    {
        var dir = FuzzTestPaths.NewTempDir();
        try
        {
            var bytes = "SELECT 1"u8.ToArray();
            File.WriteAllBytes(Path.Combine(dir, "id:000000,sig:06"), bytes);
            File.WriteAllText(Path.Combine(dir, "README.txt"), "afl readme");

            var report = ReplayRunner.Run([dir], new HashSet<string>(), presenceIsFinding: true, target: _ => { });
            report.Entries.Count.ShouldBe(1, "README.txt is ignored");
            report.Entries[0].Status.ShouldBe(ReplayStatus.NotReproduced);
            report.ExitCode.ShouldBe(1);

            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            ReplayRunner.Run([dir], new HashSet<string> { sha }, presenceIsFinding: true, target: _ => { }).ExitCode.ShouldBe(0);
            ReplayRunner.Run([Path.Combine(dir, "missing")], presenceIsFinding: true).ExitCode.ShouldBe(1, "a missing path is rejected (fail-closed)");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-20
    public void Replay_RejectsOversizedInputAndSymlinks()
    {
        var dir = FuzzTestPaths.NewTempDir();
        var outside = FuzzTestPaths.NewTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "ok.sql"), "SELECT 1"u8.ToArray());
            File.WriteAllBytes(Path.Combine(dir, "big.sql"), new byte[FuzzTarget.MaxInputBytes + 1]);
            File.WriteAllBytes(Path.Combine(dir, "exact.sql"), Enumerable.Repeat((byte)' ', FuzzTarget.MaxInputBytes).ToArray());
            File.WriteAllText(Path.Combine(outside, "secret.sql"), "SELECT 2");
            File.CreateSymbolicLink(Path.Combine(dir, "link-file.sql"), Path.Combine(outside, "secret.sql"));
            Directory.CreateSymbolicLink(Path.Combine(dir, "link-dir"), outside);

            var report = ReplayRunner.Run([dir], target: _ => { });

            FuzzTarget.MaxInputBytes.ShouldBe(65_536);
            var rejected = report.Entries.Where(e => e.Status == ReplayStatus.Rejected).Select(e => Path.GetFileName(e.Path)).Order(StringComparer.Ordinal).ToList();
            rejected.ShouldBe(["big.sql", "link-dir", "link-file.sql"]);
            report.Entries.Where(e => e.Status == ReplayStatus.Accepted).Select(e => Path.GetFileName(e.Path)).Order(StringComparer.Ordinal).ShouldBe(["exact.sql", "ok.sql"]);
            report.ExitCode.ShouldBe(1);

            // A symlink given directly as the replay path is rejected as well.
            ReplayRunner.Run([Path.Combine(dir, "link-file.sql")], target: _ => { }).Entries.Single().Status.ShouldBe(ReplayStatus.Rejected);
        }
        finally
        {
            Directory.Delete(dir, true);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-20
    public void FuzzTarget_CapsInputAndIgnoresInvalidUtf8()
    {
        FuzzTarget.ReadCapped(new MemoryStream(new byte[FuzzTarget.MaxInputBytes * 2])).Length.ShouldBe(FuzzTarget.MaxInputBytes);

        // Neither oversized nor invalid UTF-8 input reaches the engine.
        FuzzRunner.RunOne(new byte[FuzzTarget.MaxInputBytes + 1]).Outcome.ShouldBe(FuzzOutcome.Accepted);
        FuzzRunner.RunOne([0xFF, 0xFE, 0xC3, 0x28]).Outcome.ShouldBe(FuzzOutcome.Accepted);
        FuzzRunner.RunOne("SELECT id FROM orders WHERE id > 1"u8.ToArray()).Outcome.ShouldBe(FuzzOutcome.Accepted);
        FuzzRunner.RunOne("SELECT FROM WHERE ((("u8.ToArray()).Outcome.ShouldBe(FuzzOutcome.Accepted);
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-PF-07
    public void FuzzTarget_DfaReset_OnlyBetweenInputsAndNeverMasksOom()
    {
        // Deterministic boundary: the reset fires exactly on every interval-th execution, never earlier.
        var resets = 0;
        var gate = new FuzzDfaReset(3, () => resets++);
        gate.OnInputBoundary(false).ShouldBeFalse();
        gate.OnInputBoundary(false).ShouldBeFalse();
        gate.OnInputBoundary(false).ShouldBeTrue();
        gate.OnInputBoundary(false).ShouldBeFalse();
        resets.ShouldBe(1);
        gate.Executions.ShouldBe(4);

        // A live abandoned input thread defers the reset to the next safe boundary (no race with the DFA).
        var deferred = new FuzzDfaReset(1, () => resets++);
        deferred.OnInputBoundary(true).ShouldBeFalse();
        resets.ShouldBe(1);
        deferred.OnInputBoundary(false).ShouldBeTrue();
        resets.ShouldBe(2);

        // Between inputs only: the reset runs on the calling thread before the input thread starts, never while an input runs.
        var inputRunning = false;
        var violated = false;
        var caller = Environment.CurrentManagedThreadId;
        var between = new FuzzDfaReset(1, () => violated |= Volatile.Read(ref inputRunning) || Environment.CurrentManagedThreadId != caller);
        for (var i = 0; i < 3; i++)
        {
            FuzzRunner.RunOne("SELECT 1"u8.ToArray(), _ => { Volatile.Write(ref inputRunning, true); Volatile.Write(ref inputRunning, false); }, between);
        }

        violated.ShouldBeFalse();
        between.Executions.ShouldBe(3);

        // An OutOfMemoryException of the reset is never swallowed and the input does not run.
        var ran = false;
        var oom = new FuzzDfaReset(1, () => throw new OutOfMemoryException());
        Should.Throw<OutOfMemoryException>(() => FuzzRunner.RunOne([1], _ => ran = true, oom));
        ran.ShouldBeFalse();

        // The real reset clears the shared DFA and parsing still works afterwards.
        new FuzzDfaReset(1).OnInputBoundary(false).ShouldBeTrue();
        FuzzRunner.RunOne("SELECT id FROM orders WHERE id > 1"u8.ToArray(), null, new FuzzDfaReset(1)).Outcome.ShouldBe(FuzzOutcome.Accepted);
        FuzzDfaReset.DefaultInterval.ShouldBe(250_000);

        var source = File.ReadAllText(Path.Combine(FuzzTestPaths.FuzzProject, "Target", "FuzzDfaReset.cs"));
        source.ShouldNotContain("catch");
        source.ShouldNotContain("DateTime");
        source.ShouldNotContain("Stopwatch");
        source.ShouldNotContain("Random");
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-20
    public void FuzzTarget_PerformsNoIoOrNetwork()
    {
        var dir = Path.Combine(FuzzTestPaths.FuzzProject, "Target");
        var files = Directory.GetFiles(dir, "*.cs");
        files.Length.ShouldBeGreaterThanOrEqualTo(4, "vacuous scan guard");

        foreach (var file in files)
        {
            ForbiddenIo(File.ReadAllText(file)).ShouldBeEmpty($"{Path.GetFileName(file)} must perform no file, network or process I/O");
        }

        // negative self-check
        ForbiddenIo("File.ReadAllText(x)").Count.ShouldBe(1);
        ForbiddenIo("using System.Net.Http; new HttpClient()").Count.ShouldBe(2);
        ForbiddenIo("Process.Start(x); Directory.GetFiles(x); new FileStream(a, b)").Count.ShouldBe(3);
        ForbiddenIo("var s = new MemoryStream(); // no File.Open here").ShouldBeEmpty();
    }

    [GeneratedRegex(@"\b(File|Directory|FileStream|StreamReader|StreamWriter|HttpClient|Socket|TcpClient|Process|ProcessStartInfo|DriveInfo|FileInfo|DirectoryInfo)\b|System\.Net|System\.IO\.(File|Directory|Path)|GetEnvironmentVariable")]
    private static partial Regex IoToken();

    private static List<string> ForbiddenIo(string source)
    {
        var withoutComments = Regex.Replace(source, @"//[^\n]*|/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return IoToken().Matches(withoutComments).Select(m => m.Value).ToList();
    }

    // ---------- seeds ----------

    [Fact]
    public void ExportSeeds_IsDeterministic()
    {
        var a = FuzzTestPaths.NewTempDir();
        var b = FuzzTestPaths.NewTempDir();
        try
        {
            SeedCorpus.Export(FuzzTestPaths.Dql, a);
            SeedCorpus.Export(FuzzTestPaths.Dql, b);

            Snapshot(a).ShouldBe(Snapshot(b));
            Snapshot(a).Count.ShouldBeGreaterThanOrEqualTo(250);

            // The committed corpus is exactly this export (plus the dictionary).
            var committed = Snapshot(FuzzTestPaths.Corpus).Where(p => !p.Key.EndsWith(".dict", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value);
            committed.Keys.Order(StringComparer.Ordinal).ShouldBe(Snapshot(a).Keys.Order(StringComparer.Ordinal));
            foreach (var (name, hash) in Snapshot(a))
            {
                committed[name].ShouldBe(hash, $"{name} differs from the export; re-run --export-seeds");
            }

            SeedCorpus.BuildDictionary().ShouldBe(File.ReadAllText(Path.Combine(FuzzTestPaths.Corpus, "sql.dict")), "re-run --export-dict");
        }
        finally
        {
            Directory.Delete(a, true);
            Directory.Delete(b, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-13
    public void Seeds_DeriveOnlyFromOwnDqlAndHandWrittenText()
    {
        var dql = File.ReadAllText(FuzzTestPaths.Dql).Replace("\r\n", "\n", StringComparison.Ordinal);
        var ownStatements = SeedCorpus.ParseDql(dql).Select(s => s.Sql).ToHashSet(StringComparer.Ordinal);
        var hand = SeedCorpus.HandWritten.ToHashSet(StringComparer.Ordinal);
        var files = Directory.GetFiles(FuzzTestPaths.Corpus, "*.sql");

        files.Length.ShouldBeInRange(250, 350, "small seed set");
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            new FileInfo(file).Length.ShouldBeLessThanOrEqualTo(SeedCorpus.MaxSeedBytes, name);
            text.ShouldNotContain("\r");
            (name.StartsWith("dql-", StringComparison.Ordinal) ? ownStatements.Contains(text) : name.StartsWith("hand-", StringComparison.Ordinal) && hand.Contains(text))
                .ShouldBeTrue($"{name} is neither a dql.sql statement nor a hand-written seed");
        }

        // No sqllogictest text: the banner of the SLT files never appears in a seed.
        files.Select(File.ReadAllText).ShouldAllBe(t => !t.Contains("sqllogictest", StringComparison.OrdinalIgnoreCase));
        Directory.GetFiles(FuzzTestPaths.Corpus).Select(Path.GetFileName).Order(StringComparer.Ordinal).Last().ShouldBe("sql.dict");
    }

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.GetFiles(dir).ToDictionary(f => Path.GetFileName(f), f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.Ordinal);

    // ---------- quarantine ----------

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-17
    public void Fuzz_QuarantineEntriesHaveIssueNumberAndAge()
    {
        var today = new DateOnly(2026, 10, 24);
        var sha = new string('c', 64);
        string Entry(string fields) => "[{" + fields + "}]";
        var ok = Entry($"\"sha256\":\"{sha}\",\"issue\":7,\"reason\":\"r\",\"approvedOn\":\"2026-10-01\"");

        FuzzQuarantine.Parse("[]", today).ShouldBeEmpty();
        FuzzQuarantine.Parse(ok, today).ShouldContain(sha);

        string[] bad =
        [
            Entry($"\"sha256\":\"{sha}\",\"reason\":\"r\",\"approvedOn\":\"2026-10-01\""),
            Entry($"\"sha256\":\"{sha}\",\"issue\":0,\"reason\":\"r\",\"approvedOn\":\"2026-10-01\""),
            Entry($"\"sha256\":\"{sha}\",\"issue\":7,\"reason\":\"r\",\"approvedOn\":\"2026-07-01\""),
            Entry($"\"sha256\":\"{sha}\",\"issue\":7,\"reason\":\"r\",\"approvedOn\":\"2026-11-01\""),
            Entry($"\"sha256\":\"{sha}\",\"issue\":7,\"reason\":\"r\""),
            Entry($"\"sha256\":\"{sha}\",\"issue\":7,\"reason\":\"\",\"approvedOn\":\"2026-10-01\""),
            Entry($"\"sha256\":\"{sha}\",\"issue\":7,\"reason\":\"r\",\"approvedOn\":\"2026-10-01\",\"x\":1"),
            Entry($"\"sha256\":\"{sha.ToUpperInvariant()}\",\"issue\":7,\"reason\":\"r\",\"approvedOn\":\"2026-10-01\""),
            "{}",
            ok.TrimEnd(']') + "," + ok.TrimStart('['),
        ];
        foreach (var json in bad)
        {
            Should.Throw<InvalidOperationException>(() => FuzzQuarantine.Parse(json, today), json);
        }

        // The committed file passes the strict rules of both parsers, and they agree on the shared examples.
        var committed = File.ReadAllText(Path.Combine(FuzzTestPaths.Findings, "quarantine.json"));
        FuzzQuarantine.Parse(committed, DateOnly.FromDateTime(DateTime.UtcNow));
        SimQuarantine.Expired(SimQuarantine.Parse(committed), DateOnly.FromDateTime(DateTime.UtcNow)).ShouldBeEmpty();
        SimQuarantine.Parse(ok).Count.ShouldBe(1);
        foreach (var json in bad.Where(j => !j.Contains("2026-07-01", StringComparison.Ordinal) && !j.Contains("2026-11-01", StringComparison.Ordinal)))
        {
            Should.Throw<InvalidOperationException>(() => SimQuarantine.Parse(json), json);
        }
    }

    // ---------- script ----------

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-16
    public void FuzzScript_RefusesInstrumentingOutsideRunnerTemp()
    {
        var temp = FuzzTestPaths.NewTempDir();
        var outside = FuzzTestPaths.NewTempDir();
        try
        {
            Run("--check-paths", temp).ExitCode.ShouldBe(0);
            Run("--check-paths", temp).Output.ShouldContain(Path.Combine(temp, "fuzz-bin"));

            Run("--check-paths", null).ExitCode.ShouldBe(1);
            Run("--check-paths", "/").ExitCode.ShouldBe(1);
            Run("--check-paths", "relative/dir").ExitCode.ShouldBe(1);
            Run("--check-paths", temp + "/../x").ExitCode.ShouldBe(1);
            Run("--check-paths", "/tmp").ExitCode.ShouldBe(1, "a top-level directory is refused");
            Run("--check-paths", Path.Combine(temp, "does-not-exist")).ExitCode.ShouldBe(1);

            // fuzz-bin is a symlink that leaves RUNNER_TEMP: refused before anything is built, deleted or instrumented.
            Directory.CreateSymbolicLink(Path.Combine(temp, "fuzz-bin"), outside);
            var linked = Run("--check-paths", temp);
            linked.ExitCode.ShouldBe(1);
            linked.Output.ShouldContain("refusing path outside RUNNER_TEMP");
            Run("5", temp).ExitCode.ShouldBe(1);
            Directory.GetFileSystemEntries(outside).ShouldBeEmpty();

            // Argument validation: the run time and the memory limit are bounded, never unlimited.
            File.Delete(Path.Combine(temp, "fuzz-bin"));
            foreach (var arg in new[] { "0", "7201", "-5", "abc", "5;ls", "" })
            {
                Run(arg, temp).ExitCode.ShouldBe(1, arg);
            }

            foreach (var mem in new[] { "none", "0", "512", "99999", "2048;x" })
            {
                Run("--check-paths", temp, mem).ExitCode.ShouldBe(1, mem);
            }

            Run("--check-paths", temp, "4096").ExitCode.ShouldBe(0);
        }
        finally
        {
            Directory.Delete(temp, true);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    // SEC-SIM-09
    public void FuzzScript_PinsToolAndFiniteMemory_AndInstrumentsOnlyTheRunnerTempCopy()
    {
        var script = File.ReadAllText(FuzzTestPaths.Script);
        var lines = script.Split('\n').Where(l => !l.TrimStart().StartsWith('#')).ToList();

        lines.Count(l => Regex.IsMatch(l, @"\bdotnet\s+tool\s+install\s+SharpFuzz\.CommandLine\s+--version\s+2\.3\.0\b")).ShouldBe(1);
        lines.Count(l => Regex.IsMatch(l, @"\bdotnet\s+tool\s+install\b")).ShouldBe(1);
        lines.Count(l => Regex.IsMatch(l, @"^\s*""\$TOOL_DIR/sharpfuzz""\s+""\$RUNNER_TEMP/fuzz-bin/TrinoSqlEngine\.dll""\s*$")).ShouldBe(1);
        lines.Count(l => Regex.IsMatch(l, @"sharpfuzz""?\s", RegexOptions.IgnoreCase) && !l.Contains("dotnet tool install", StringComparison.Ordinal)).ShouldBe(1);

        var afl = lines.Single(l => Regex.IsMatch(l, @"^\s*afl-fuzz\s"));
        afl.ShouldContain("-m \"$SIM_AFL_MEM_MB\"");
        afl.ShouldContain("-t 5000");
        afl.ShouldContain("-G 65536");
        afl.ShouldContain("-s \"$SEED\"");
        afl.ShouldContain("-V \"$MODE\"");
        afl.ShouldContain("-x \"$CORPUS/sql.dict\"");
        afl.ShouldContain("--afl");
        script.ShouldNotContain("-m none");
        lines.ShouldNotContain(l => Regex.IsMatch(l, @"\b(curl|wget)\b|pip3?\s+install|:latest\b|--version\s+latest"));
    }

    private static (int ExitCode, string Output) Run(string argument, string? runnerTemp, string? memory = null)
    {
        var info = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = FuzzTestPaths.RepoRoot };
        info.ArgumentList.Add(FuzzTestPaths.Script);
        info.ArgumentList.Add(argument);
        info.Environment.Remove("RUNNER_TEMP");
        info.Environment.Remove("SIM_AFL_MEM_MB");
        info.Environment.Remove("AUTHERIS_SIM_SEED");
        if (runnerTemp is not null)
        {
            info.Environment["RUNNER_TEMP"] = runnerTemp;
        }

        if (memory is not null)
        {
            info.Environment["SIM_AFL_MEM_MB"] = memory;
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
