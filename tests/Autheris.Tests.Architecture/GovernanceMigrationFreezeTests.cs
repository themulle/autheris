using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Issue #388 (WP-388-2, plan D-1 and D-9): the v1.5.0 governance baseline is frozen, released steps keep their checksum, and every
/// step exists for all engines. Changing the schema needs a new step, never an edit of a released artefact.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class GovernanceMigrationFreezeTests(ITestOutputHelper output)
{
    private static readonly string[] Engines = ["sqlite", "postgresql", "sqlserver"];
    private const string Migrations = "src/Autheris.Infrastructure/Persistence/Migrations/";
    private const string BaselineGolden = "tests/Autheris.Tests.Architecture/Golden/GovernanceBaseline/v1.5.0.sha256";
    private const string ImmutabilityScript = ".github/scripts/check-governance-immutability.sh";

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Autheris.sln was not found above the test output directory.");
        return dir.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative));

    private static string Sha256Lf(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.TrimStart('﻿').ReplaceLineEndings("\n"))));

    private static Dictionary<string, string> ReadHashGolden() =>
        Read(BaselineGolden).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith('#'))
            .Select(l => l.Split("  ", 2))
            .ToDictionary(p => p[1], p => p[0], StringComparer.Ordinal);

    [Fact]
    public void Baseline_V150_SourcesAndSnapshotsAreByteIdentical()
    {
        var golden = ReadHashGolden();

        golden.Count.ShouldBe(6, "three baseline sources and three v0001 shape snapshots are frozen");
        foreach (var (path, expected) in golden)
        {
            Sha256Lf(Read(path)).ShouldBe(expected, $"{path} is frozen at v1.5.0 (plan D-1); a schema change needs a new migration step, not an edit.");
        }

        foreach (var engine in Engines)
        {
            golden.Keys.ShouldContain($"{Migrations}Shape/governance-shape.{engine}.v0001.json");
        }
    }

    [Fact]
    public void Baseline_Sources_MatchTheV150Tag_WhenTheTagIsAvailable()
    {
        // I388-SC-03: the source hashes of the golden come from 'git show v1.5.0:<path>'.
        var probe = Git(Root(), "rev-parse", "--verify", "--quiet", "v1.5.0^{commit}");
        if (probe.ExitCode != 0)
        {
            output.WriteLine("SKIPPED (limitation): tag v1.5.0 is not available in this checkout (shallow clone without tags); the in-repo golden is the only check here.");
            return;
        }

        foreach (var (path, expected) in ReadHashGolden().Where(g => g.Key.EndsWith("BaselineSchema.cs", StringComparison.Ordinal)))
        {
            var show = Git(Root(), "show", $"v1.5.0:{path}");
            show.ExitCode.ShouldBe(0);
            Sha256Lf(show.Output).ShouldBe(expected, $"{path} must be byte-identical to the v1.5.0 tag");
        }
    }

    [Fact]
    public void Baseline_Files_ReferenceNoOtherAutherisTypes()
    {
        // The frozen hash stays meaningful only while the baseline files depend on nothing that can change under them.
        foreach (var engine in new[] { "Sqlite", "PostgreSql", "SqlServer" })
        {
            var source = Read($"{Migrations}{engine}BaselineSchema.cs");
            source.Split('\n').Where(l => l.TrimStart().StartsWith("using ", StringComparison.Ordinal))
                .ShouldAllBe(l => !l.Contains("Autheris", StringComparison.Ordinal), $"{engine}BaselineSchema.cs imports no Autheris namespace");
            AutherisReference().Matches(source).Select(m => m.Value).Where(v => !v.StartsWith("Autheris.Infrastructure.Persistence.Migrations", StringComparison.Ordinal))
                .ShouldBeEmpty($"{engine}BaselineSchema.cs references no other Autheris type");
        }
    }

    [Fact]
    public void ReleasedStepChecksums_AreUnchanged()
    {
        foreach (var engine in Engines)
        {
            var golden = Read($"tests/Autheris.Tests.Architecture/Golden/GovernanceSteps/checksums.{engine}.txt")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.StartsWith('#'))
                .Select(l => l.Split(' '))
                .ToList();
            golden.ShouldAllBe(p => p.Length == 3, "each golden line is 'version name checksum'");

            var catalogue = Catalogue(engine);
            golden.Select(p => p[0]).ShouldBe(catalogue.Select(c => c.Version), $"{engine}: released steps are neither removed nor reordered and every catalogue step is listed");
            foreach (var (line, step) in golden.Zip(catalogue))
            {
                line[1].ShouldBe(step.Name);
                line[2].ShouldBe(step.Checksum, $"{engine} step {step.Version}: a released step is immutable; add a new step instead");
            }
        }
    }

    [Fact]
    public void EveryStep_HasSqlAndShapeDeltaForAllEngines()
    {
        foreach (var engine in Engines)
        {
            var files = StepFiles(engine);
            var sql = files.Where(f => f.EndsWith(".sql", StringComparison.Ordinal)).Select(f => f[..^4]).ToHashSet();
            var delta = files.Where(f => f.EndsWith(".shape.json", StringComparison.Ordinal)).Select(f => f[..^11]).ToHashSet();
            sql.SetEquals(delta).ShouldBeTrue($"{engine}: every step needs NNNN_name.sql and NNNN_name.shape.json");
            files.ShouldAllBe(f => StepFile().IsMatch(f), $"{engine}: unexpected file in the Steps folder");
        }
    }

    [Fact]
    public void Catalogues_HaveTheSameStepsForAllProviders()
    {
        var byEngine = Engines.ToDictionary(e => e, e => StepFiles(e).Where(f => f.EndsWith(".sql", StringComparison.Ordinal)).Select(f => f[..^4]).Order().ToArray());

        byEngine["postgresql"].ShouldBe(byEngine["sqlite"], "step numbers and names must be equal in all dialects");
        byEngine["sqlserver"].ShouldBe(byEngine["sqlite"], "step numbers and names must be equal in all dialects");
        byEngine["sqlite"].Select((s, i) => int.Parse(s[..4])).ShouldBe(Enumerable.Range(2, byEngine["sqlite"].Length), "file steps are gap-free from 0002");
    }

    [Fact]
    public void NoGovernanceDdl_OutsideMigrations()
    {
        var ddl = DdlLiteral();
        var allowlist = new HashSet<string>(StringComparer.Ordinal)
        {
            "src/Autheris.Infrastructure/Caching/TableCache/InMemoryTableCacheStore.cs",
            "src/Autheris.Infrastructure/Caching/TableCache/DuckDbPersistentTableCacheStore.cs"
        };
        var root = Root();
        var hits = Directory.EnumerateFiles(Path.Combine(root, "src/Autheris.Infrastructure"), "*.cs", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Where(p => !p.Contains("/obj/", StringComparison.Ordinal) && !p.Contains("/bin/", StringComparison.Ordinal))
            .Where(p => !p.StartsWith(Migrations, StringComparison.Ordinal))
            .Where(p => ddl.IsMatch(StripComments(File.ReadAllText(Path.Combine(root, p)))))
            .ToArray();

        hits.Except(allowlist).ShouldBeEmpty("governance DDL belongs to Persistence/Migrations steps only (issue #388); a new allowlist entry needs an owner decision");
        allowlist.ShouldAllBe(a => File.Exists(Path.Combine(root, a)), "the allowlist lists existing files only");
    }

    [Fact]
    public void GoldenAppendOnly_AgainstBaseBranch_FailsOnModifiedOrDeletedLines()
    {
        // I388-SC-02, script test on a temporary git repository.
        var repo = Path.Combine(Path.GetTempPath(), "i388-immutability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            string[] golden = ["tests/Autheris.Tests.Architecture/Golden/GovernanceSteps/checksums.sqlite.txt"];
            const string step = "src/Autheris.Infrastructure/Persistence/Migrations/Steps/sqlite/0002_a.sql";
            const string baseline = "src/Autheris.Infrastructure/Persistence/Migrations/SqliteBaselineSchema.cs";
            Run(repo, "init", "-q", "-b", "main");
            Run(repo, "config", "user.email", "t@example.org");
            Run(repo, "config", "user.name", "t");
            Write(repo, golden[0], "0001 baseline AAA\n0002 a BBB\n");
            Write(repo, step, "CREATE TABLE A (x INT);\n");
            Write(repo, baseline, "// baseline\n");
            Run(repo, "add", "-A");
            Run(repo, "commit", "-q", "-m", "base");

            Scenario("append a golden line and add a new step", () =>
            {
                Write(repo, golden[0], "0001 baseline AAA\n0002 a BBB\n0003 b CCC\n");
                Write(repo, "src/Autheris.Infrastructure/Persistence/Migrations/Steps/sqlite/0003_b.sql", "CREATE TABLE B (x INT);\n");
            }, expectedExit: 0);
            Scenario("modify a golden line", () => Write(repo, golden[0], "0001 baseline XXX\n0002 a BBB\n"), expectedExit: 1);
            Scenario("delete a golden line", () => Write(repo, golden[0], "0001 baseline AAA\n"), expectedExit: 1);
            Scenario("delete the golden", () => File.Delete(Path.Combine(repo, golden[0])), expectedExit: 1);
            Scenario("modify a released step", () => Write(repo, step, "CREATE TABLE A (x TEXT);\n"), expectedExit: 1);
            Scenario("delete a released step", () => File.Delete(Path.Combine(repo, step)), expectedExit: 1);
            Scenario("modify the baseline source", () => Write(repo, baseline, "// edited\n"), expectedExit: 1);

            void Scenario(string name, Action change, int expectedExit)
            {
                Run(repo, "checkout", "-q", "-B", "pr", "main");
                change();
                Run(repo, "add", "-A");
                Run(repo, "commit", "-q", "--allow-empty", "-m", name);
                var result = RunScript(repo, "main", "pr");
                result.ExitCode.ShouldBe(expectedExit, $"scenario '{name}': {result.Output}");
                Run(repo, "checkout", "-q", "main");
            }
        }
        finally
        {
            ForceDelete(repo);
        }
    }

    [Fact]
    public void GoldenAndStepsAreAppendOnly_AgainstTheBaseBranchOfThisCheckout()
    {
        var root = Root();
        var baseRef = Git(root, "rev-parse", "--verify", "--quiet", "origin/main");
        if (baseRef.ExitCode != 0 || Git(root, "merge-base", "HEAD", "origin/main").ExitCode != 0)
        {
            output.WriteLine("SKIPPED (limitation): origin/main or the merge base is not available in this checkout (shallow clone); the base-branch immutability check is not enforced here and needs a CI job with full history that runs " + ImmutabilityScript + " origin/<base>.");
            return;
        }

        var result = RunScript(root, "origin/main", "HEAD");
        result.ExitCode.ShouldBe(0, result.Output);
    }

    private static IReadOnlyList<(string Version, string Name, string Checksum)> Catalogue(string engine)
    {
        var steps = new List<(string, string, string)>
        {
            ("0001", "baseline_governance_schema", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{engine}:0001:baseline_governance_schema:v1"))))
        };
        foreach (var file in StepFiles(engine).Where(f => f.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var match = StepFile().Match(file);
            var text = Read($"{Migrations}Steps/{engine}/{file}").TrimStart('﻿').ReplaceLineEndings("\n");
            steps.Add((match.Groups[1].Value, match.Groups[2].Value,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{engine}:{match.Groups[1].Value}:{match.Groups[2].Value}:" + text)))));
        }

        return steps;
    }

    private static string[] StepFiles(string engine)
    {
        var dir = Path.Combine(Root(), Migrations, "Steps", engine);
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Select(Path.GetFileName).Where(f => f != ".gitkeep").Select(f => f!).Order(StringComparer.Ordinal).ToArray()
            : [];
    }

    private static void Write(string repo, string relative, string content)
    {
        var path = Path.Combine(repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static void Run(string dir, params string[] args) => Git(dir, args).ExitCode.ShouldBe(0, $"git {string.Join(' ', args)}");

    private static (int ExitCode, string Output) Git(string dir, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private (int ExitCode, string Output) RunScript(string repo, string baseRef, string headRef)
    {
        var info = new ProcessStartInfo("bash") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(Root(), ImmutabilityScript));
        info.ArgumentList.Add(baseRef);
        info.ArgumentList.Add(headRef);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static void ForceDelete(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    [GeneratedRegex(@"^(\d{4})_([a-z0-9_]+)\.(sql|shape\.json)$|^\.gitkeep$")]
    private static partial Regex StepFile();

    [GeneratedRegex(@"\bAutheris\.[A-Za-z0-9_.]+")]
    private static partial Regex AutherisReference();

    private static string StripComments(string text) =>
        Regex.Replace(Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline), @"(?m)^\s*//.*$", string.Empty);

    [GeneratedRegex(@"\b(CREATE\s+(UNIQUE\s+)?(TABLE|INDEX)|ALTER\s+TABLE|DROP\s+TABLE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DdlLiteral();
}
