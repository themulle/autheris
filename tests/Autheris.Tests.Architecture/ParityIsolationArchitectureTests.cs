using System.Diagnostics;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// REQ-PARITY-NIGHTLY D-3/D-4 and SEC-PAR-01..04/06: the nightly parity suite must be reachable only from
/// <c>parity-nightly.yml</c>. The scanners fail closed (a missing repo root, workflow directory or zero matched lines is a failure) and each
/// scanner has a negative self-check on an in-memory sample.
/// </summary>
public sealed partial class ParityIsolationArchitectureTests
{
    private const string ParityProject = "Autheris.Tests.Parity";
    private const string NightlyWorkflow = "parity-nightly.yml";
    private const string PlanPath = "docs/plans/2026-10-23-plan-nightly-parity-tests.md";

    [GeneratedRegex(@"\\\r?\n\s*")]
    private static partial Regex Continuation();

    [GeneratedRegex(@"\buses:\s*(?<action>[^@\s]+)@(?<ref>[^\s#]+)")]
    private static partial Regex UsesPattern();

    [GeneratedRegex(@"\bPARITY-GAP-\d{3}\b")]
    private static partial Regex GapIdPattern();

    [GeneratedRegex(@"^###\s+(PARITY-GAP-\d{3})\s*$", RegexOptions.Multiline)]
    private static partial Regex GapHeadingPattern();

    [GeneratedRegex(@"BASE_FILTER\s*=\s*""(?<filter>[^""]*)""")]
    private static partial Regex BaseFilterPattern();

    // ---------- scanners (pure, testable on samples) ----------

    internal static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Autheris.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Autheris.sln not found: the parity isolation guard fails closed.");
    }

    internal static IReadOnlyDictionary<string, string> ReadWorkflows(string root)
    {
        var dir = Path.Combine(root, ".github", "workflows");
        if (!Directory.Exists(dir))
        {
            throw new InvalidOperationException(".github/workflows not found: the parity isolation guard fails closed.");
        }

        var files = Directory.GetFiles(dir, "*.yml");
        if (files.Length == 0)
        {
            throw new InvalidOperationException("No workflow files found: the parity isolation guard fails closed.");
        }

        return files.ToDictionary(f => Path.GetFileName(f)!, File.ReadAllText);
    }

    private static string ReadWorkflow(string name) =>
        ReadWorkflows(FindRepoRoot()).TryGetValue(name, out var text) ? text : throw new InvalidOperationException($"{name} not found.");

    internal static IReadOnlyList<string> JoinedLines(string text) => Continuation().Replace(text, " ").Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    /// <summary>Joined <c>dotnet test ... Autheris.sln</c> lines; throws when none is found (a vacuous pass is a failure, SEC-PAR-04).</summary>
    internal static IReadOnlyList<string> SolutionTestLines(IEnumerable<string> workflowTexts)
    {
        var lines = workflowTexts.SelectMany(JoinedLines).Where(l => l.Contains("dotnet test", StringComparison.Ordinal) && l.Contains("Autheris.sln", StringComparison.Ordinal)).ToList();
        return lines.Count == 0 ? throw new InvalidOperationException("No 'dotnet test Autheris.sln' line matched: vacuous guard.") : lines;
    }

    internal static IReadOnlyList<string> LinesMissingParityExclusion(IReadOnlyList<string> lines) =>
        lines.Where(l => !l.Contains("Category!=Parity", StringComparison.Ordinal)).ToList();

    /// <summary>Top-level trigger keys of the workflow (children of the <c>on:</c> block).</summary>
    internal static IReadOnlyList<string> TriggerKeys(string workflow)
    {
        var keys = new List<string>();
        var inOn = false;
        foreach (var raw in workflow.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (Regex.IsMatch(line, @"^on:\s*$"))
            {
                inOn = true;
                continue;
            }

            if (inOn && line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#'))
            {
                break;
            }

            var m = Regex.Match(line, @"^  ([A-Za-z_]+):");
            if (inOn && m.Success)
            {
                keys.Add(m.Groups[1].Value);
            }
        }

        return keys.Count == 0 ? throw new InvalidOperationException("No trigger keys parsed from the workflow.") : keys;
    }

    internal static IReadOnlyList<string> ParityCategoryUsesOutsideParityProject(string testsRoot)
    {
        var needle = new Regex("\"Category\"\\s*,\\s*\"" + "Parity\"");
        if (!Directory.Exists(testsRoot))
        {
            throw new InvalidOperationException("tests directory not found.");
        }

        return Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f) && !f.Contains(Path.DirectorySeparatorChar + ParityProject + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !f.EndsWith("ParityIsolationArchitectureTests.cs", StringComparison.Ordinal))
            .Where(f => needle.IsMatch(File.ReadAllText(f)))
            .ToList();
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    internal static IReadOnlyList<string> ParityClassesWithoutBase(string source)
    {
        var bad = new List<string>();
        var trait = new Regex(@"\[Trait\(""Category"", ""Parity""\)\]");
        foreach (Match m in trait.Matches(source))
        {
            var rest = source[(m.Index + m.Length)..];
            var header = Regex.Match(rest, @"\bclass\s+(?<name>\w+)(?<tail>[^{;]*)[{;]");
            if (!header.Success || !Regex.IsMatch(header.Groups["tail"].Value, @":\s*(?:[\w.]+\s*,\s*)*ParityTestBase\b"))
            {
                bad.Add(header.Success ? header.Groups["name"].Value : "<unparsed>");
            }
        }

        return bad;
    }

    internal static IReadOnlyList<string> SecurityTestsWithKnownGap(string source)
    {
        // member level: one attribute stack carries both the Security trait and a KnownGap
        var violations = Regex.Matches(source, @"(?<stack>(?:\s*\[[^\]\r\n]*\])+)\s*(?:public|internal|private|protected)\b")
            .Select(m => m.Groups["stack"].Value)
            .Where(s => s.Contains("KnownGap", StringComparison.Ordinal) && s.Contains("\"Security\"", StringComparison.Ordinal))
            .ToList();

        // class level: a Security trait on the class forbids a KnownGap on any member declared before the next class
        var classes = Regex.Matches(source, @"(?<stack>(?:\s*\[[^\]\r\n]*\])+)\s*(?:\w+\s+)*?(?:class|record|struct)\s+\w+").ToList();
        for (var i = 0; i < classes.Count; i++)
        {
            if (!classes[i].Groups["stack"].Value.Contains("\"Security\"", StringComparison.Ordinal))
            {
                continue;
            }

            var end = i + 1 < classes.Count ? classes[i + 1].Index : source.Length;
            var body = source[(classes[i].Index + classes[i].Length)..end];
            if (body.Contains("[KnownGap(", StringComparison.Ordinal))
            {
                violations.Add(classes[i].Value.Trim());
            }
        }

        return violations;
    }

    private static IEnumerable<string> ParitySourceFiles(string root)
    {
        var dir = Path.Combine(root, "tests", ParityProject);
        if (!Directory.Exists(dir))
        {
            throw new InvalidOperationException("Parity project directory not found: the guard fails closed.");
        }

        return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)).ToList();
    }

    // ---------- D-4 isolation tests ----------

    [Fact]
    [Trait("Category", "Security")]
    public void ParityIsolation_ProjectIsNotInAnySolution()
    {
        var root = FindRepoRoot();
        var solutions = Directory.GetFiles(root, "*.sln").Concat(Directory.GetFiles(root, "*.slnf")).Concat(Directory.GetFiles(root, "*.slnx")).ToList();
        solutions.ShouldNotBeEmpty();
        solutions.Where(f => File.ReadAllText(f).Contains(ParityProject, StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityIsolation_OnlyParityWorkflowTargetsParityProject()
    {
        var workflows = ReadWorkflows(FindRepoRoot());
        workflows.ShouldContainKey(NightlyWorkflow);
        workflows.Where(w => w.Key != NightlyWorkflow && w.Value.Contains(ParityProject, StringComparison.Ordinal)).Select(w => w.Key).ShouldBeEmpty();
        workflows[NightlyWorkflow].ShouldContain(ParityProject);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityIsolation_SolutionWideTestRunsExcludeParity()
    {
        var lines = SolutionTestLines(ReadWorkflows(FindRepoRoot()).Values);
        LinesMissingParityExclusion(lines).ShouldBeEmpty();

        // negative self-check
        LinesMissingParityExclusion(SolutionTestLines(["      run: dotnet test Autheris.sln --filter \"Category!=Spark\""])).Count.ShouldBe(1);
        LinesMissingParityExclusion(SolutionTestLines(["      run: dotnet test Autheris.sln --filter \"Category!=Spark&Category!=Parity\""])).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityIsolation_GuardFailsOnZeroMatchedTestLines()
    {
        Should.Throw<InvalidOperationException>(() => SolutionTestLines(["run: echo hello", "run: dotnet build Autheris.sln"]));
        Should.Throw<InvalidOperationException>(() => SolutionTestLines([]));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityIsolation_GuardFailsWhenWorkflowsMissing()
    {
        var empty = Path.Combine(Path.GetTempPath(), "autheris-parity-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            Should.Throw<InvalidOperationException>(() => ReadWorkflows(empty));
            Directory.CreateDirectory(Path.Combine(empty, ".github", "workflows"));
            Should.Throw<InvalidOperationException>(() => ReadWorkflows(empty));
            Should.Throw<InvalidOperationException>(() => ParitySourceFiles(empty).ToList());
        }
        finally
        {
            Directory.Delete(empty, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityIsolation_ShardBaseFilterExcludesParity()
    {
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "scripts", "integration-shard-filter.py"));
        var filter = BaseFilterPattern().Match(script);
        filter.Success.ShouldBeTrue();
        filter.Groups["filter"].Value.ShouldContain("Category!=Parity");

        // negative self-check
        BaseFilterPattern().Match("BASE_FILTER = \"Category!=Spark\"").Groups["filter"].Value.ShouldNotContain("Category!=Parity");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityIsolation_NightlyWorkflowHasNoPullRequestOrPushTrigger()
    {
        var keys = TriggerKeys(ReadWorkflow(NightlyWorkflow));
        keys.ShouldBe(["schedule", "workflow_dispatch"], ignoreOrder: true);

        // negative self-check
        TriggerKeys("name: x\non:\n  pull_request:\n    branches: [ main ]\n  schedule:\n    - cron: '0 2 * * *'\njobs:\n").ShouldContain("pull_request");
        Should.Throw<InvalidOperationException>(() => TriggerKeys("name: x\njobs:\n"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityIsolation_NightlyWorkflowSetsParityRunEnv()
    {
        var text = ReadWorkflow(NightlyWorkflow);
        text.ShouldContain("AUTHERIS_PARITY_RUN: '1'");
        text.ShouldContain("cron: '0 2 * * *'");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityIsolation_NoOtherProjectUsesParityCategory()
    {
        var root = FindRepoRoot();
        ParityCategoryUsesOutsideParityProject(Path.Combine(root, "tests")).ShouldBeEmpty();

        // negative self-check
        var tmp = Path.Combine(Path.GetTempPath(), "autheris-parity-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tmp, "Other"));
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Other", "A.cs"), "[Trait(\"Category\", \"" + "Parity\")] class A {}");
            ParityCategoryUsesOutsideParityProject(tmp).Count.ShouldBe(1);
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityIsolation_EveryParityTestClassDerivesFromBase()
    {
        var files = ParitySourceFiles(FindRepoRoot()).ToList();
        files.ShouldNotBeEmpty();
        files.SelectMany(f => ParityClassesWithoutBase(File.ReadAllText(f)).Select(c => $"{Path.GetFileName(f)}:{c}")).ShouldBeEmpty();

        // negative self-check
        ParityClassesWithoutBase("[Trait(\"Category\", \"Parity\")]\npublic sealed class A : ParityTestBase { }").ShouldBeEmpty();
        ParityClassesWithoutBase("[Trait(\"Category\", \"Parity\")]\npublic sealed class B(int x) : ParityTestBase { }").ShouldBeEmpty();
        ParityClassesWithoutBase("[Trait(\"Category\", \"Parity\")]\npublic sealed class C { }").ShouldBe(["C"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityIsolation_KnownGapIdsHaveDrafts()
    {
        var root = FindRepoRoot();
        var plan = File.ReadAllText(Path.Combine(root, PlanPath));
        var headings = GapHeadingPattern().Matches(plan).Select(m => m.Groups[1].Value).ToList();
        headings.Distinct().Count().ShouldBe(headings.Count, "duplicate gap draft headings");

        var used = ParitySourceFiles(root).SelectMany(f => GapIdPattern().Matches(File.ReadAllText(f)).Select(m => m.Value)).Distinct().ToList();
        used.Except(headings).ShouldBeEmpty("gap ids used in parity sources without a '### PARITY-GAP-NNN' draft in section 5 of the plan");

        // SEC-PAR-05d: no KnownGap on Security-trait tests
        ParitySourceFiles(root).SelectMany(f => SecurityTestsWithKnownGap(File.ReadAllText(f))).ShouldBeEmpty();

        // negative self-checks
        SecurityTestsWithKnownGap("[Fact]\n[Trait(\"Category\", \"Security\")]\n[KnownGap(\"PARITY-GAP-001\", \"r\")]\npublic void T() { }").Count.ShouldBe(1);
        SecurityTestsWithKnownGap("[Fact]\n[Trait(\"Category\", \"Security\")]\npublic void T() { }").ShouldBeEmpty();
        // class-level Security trait with a KnownGap on any method of that class
        SecurityTestsWithKnownGap("[Trait(\"Category\", \"Security\")]\npublic sealed class C\n{\n    [ParityFact]\n    [KnownGap(\"PARITY-GAP-001\", \"r\")]\n    public void T() { }\n}").Count.ShouldBe(1);
        SecurityTestsWithKnownGap("[Trait(\"Category\", \"Security\")]\npublic sealed class C\n{\n    [ParityFact]\n    public void T() { }\n}\n[Trait(\"Category\", \"Parity\")]\npublic sealed class D\n{\n    [KnownGap(\"PARITY-GAP-001\", \"r\")]\n    public void U() { }\n}").ShouldBeEmpty();
        GapIdPattern().IsMatch("PARITY-GAP-0001").ShouldBeFalse();
    }

    // ---------- SEC-PAR-01..03 workflow and artifact controls ----------

    [Fact]
    [Trait("Category", "Security")]
    public void ParityWorkflow_HasOnlyScheduleAndDispatchTriggers()
    {
        // SEC-PAR-01
        var text = string.Join('\n', ReadWorkflow(NightlyWorkflow).Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
        TriggerKeys(text).ShouldBe(["schedule", "workflow_dispatch"], ignoreOrder: true);
        foreach (var forbidden in new[] { "pull_request_target", "workflow_run", "pull_request:", "push:" })
        {
            text.ShouldNotContain(forbidden);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityWorkflow_PermissionsAreLeastPrivilege()
    {
        // SEC-PAR-01
        var text = ReadWorkflow(NightlyWorkflow);
        text.ShouldContain("\npermissions: {}\n");
        Regex.Count(text, @"^\s+permissions:\s*$", RegexOptions.Multiline).ShouldBe(Regex.Count(text, @"^\s+contents: read\s*$", RegexOptions.Multiline));
        Regex.Matches(text, @"^\s+(?!contents: read)[a-z\-]+: (read|write)\s*$", RegexOptions.Multiline).ShouldBeEmpty();
        text.ShouldNotContain("secrets.");
        text.ShouldNotContain("GITHUB_TOKEN");
        Regex.Count(text, @"actions/checkout@").ShouldBe(Regex.Count(text, @"persist-credentials: false"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityWorkflow_ActionsPinnedBySha()
    {
        // SEC-PAR-01: full SHA, identical to a pin used in ci.yml or sql-compiler-gate-nightly.yml
        var workflows = ReadWorkflows(FindRepoRoot());
        var known = new[] { "ci.yml", "sql-compiler-gate-nightly.yml" }
            .SelectMany(n => UsesPattern().Matches(workflows[n]).Select(m => (m.Groups["action"].Value, m.Groups["ref"].Value)))
            .ToHashSet();
        var uses = UsesPattern().Matches(workflows[NightlyWorkflow]).Select(m => (Action: m.Groups["action"].Value, Ref: m.Groups["ref"].Value)).ToList();
        uses.ShouldNotBeEmpty();
        foreach (var (action, sha) in uses)
        {
            Regex.IsMatch(sha, "^[0-9a-f]{40}$").ShouldBeTrue($"{action}@{sha} is not pinned by full SHA");
            known.ShouldContain((action, sha), $"{action}@{sha} differs from the pins of ci.yml/sql-compiler-gate-nightly.yml");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityWorkflow_InputsOnlyViaEnvNotInRun()
    {
        // SEC-PAR-02: every expression that reads an input must be an env: assignment
        var text = ReadWorkflow(NightlyWorkflow);
        var offenders = JoinedLines(text)
            .Where(l => Regex.IsMatch(l, @"\$\{\{[^}]*(\binputs\.|github\.event\.inputs)"))
            .Where(l => !Regex.IsMatch(l, @"^\s+[A-Z][A-Z0-9_]*: \$\{\{ inputs\.\w+ \}\}\s*$"))
            .ToList();
        offenders.ShouldBeEmpty();
        text.ShouldNotContain("GITHUB_ENV");
        text.ShouldNotContain("GITHUB_OUTPUT");

        // negative self-check
        Regex.IsMatch("        run: echo ${{ inputs.filter }}", @"^\s+[A-Z][A-Z0-9_]*: \$\{\{ inputs\.\w+ \}\}\s*$").ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityWorkflow_FilterIsValidatedAndParenthesised()
    {
        // SEC-PAR-02
        var text = ReadWorkflow(NightlyWorkflow);
        text.ShouldContain("^[A-Za-z0-9_.~=\\!\\&\\|\\(\\)]*$");
        text.ShouldContain("^[0-9]{0,10}$");
        text.ShouldContain("FILTER=\"Category=Parity&Dialect!=Container\"");
        text.ShouldContain("FILTER=\"$FILTER&($PARITY_FILTER_INPUT)\"");
        text.ShouldContain("--filter \"$FILTER\"");
        text.ShouldNotContain("eval ");
        text.IndexOf("Validate inputs", StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf("Parity tests (SQLite)", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ReleaseBuildExcludesTestAuthTests_CoversParityArtifacts()
    {
        // SEC-PAR-03
        var root = FindRepoRoot();
        var workflows = ReadWorkflows(root);
        foreach (var name in new[] { "release.yml", "docker-publish.yml" })
        {
            workflows[name].ShouldNotContain(ParityProject);
            workflows[name].ShouldNotContain("parity-nightly");
        }

        var dockerfiles = Directory.GetFiles(root, "Dockerfile*", SearchOption.TopDirectoryOnly);
        dockerfiles.ShouldNotBeEmpty();
        dockerfiles.Where(f => File.ReadAllText(f).Contains(ParityProject, StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityProject_DoesNotModifyProductionDefaults()
    {
        // SEC-PAR-03
        var root = FindRepoRoot();
        var src = Path.Combine(root, "src");
        Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f))
            .Where(f => File.ReadAllText(f).Contains(ParityProject, StringComparison.Ordinal)).ShouldBeEmpty();

        var testAuthOn = new Regex(@"EnableTestAuthHandler""?\s*[:=]\s*""?true", RegexOptions.IgnoreCase);
        var productionConfig = Directory.EnumerateFiles(src, "appsettings*.json", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f))
            .Concat(Directory.GetFiles(root, "Directory.Build.props")).Concat(Directory.GetFiles(root, "Dockerfile*"));
        productionConfig.Where(f => testAuthOn.IsMatch(File.ReadAllText(f))).ShouldBeEmpty();

        var csproj = File.ReadAllText(Path.Combine(root, "tests", ParityProject, ParityProject + ".csproj"));
        csproj.ShouldNotContain("AutherisIncludeTestAuth");
    }

    // ---------- SEC-PAR-06: report script self-test ----------

    private static (int ExitCode, string Output) RunReportSelfTest()
    {
        var script = Path.Combine(FindRepoRoot(), ".github", "scripts", "parity-report.py");
        File.Exists(script).ShouldBeTrue();
        var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("--self-test");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("python3 could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(60_000).ShouldBeTrue("parity-report.py --self-test timed out");
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityReport_RejectsMalformedAndOversizedInput()
    {
        var (exit, output) = RunReportSelfTest();
        exit.ShouldBe(0, output);
        output.ShouldContain("self-test OK");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityReport_EscapesMarkdownInCellValues()
    {
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "scripts", "parity-report.py"));
        script.ShouldContain("html.escape");
        script.ShouldNotContain("shell=True");
        script.ShouldNotContain("subprocess");
        script.ShouldNotContain("eval(");
        RunReportSelfTest().ExitCode.ShouldBe(0);
    }

    // ---------- WP4: the container dialect job keeps the SEC-PAR-01/02 invariants ----------

    /// <summary>The text of one top-level job (from <c>  name:</c> to the next job); throws when the job is missing (fail closed).</summary>
    internal static string JobBlock(string workflow, string job)
    {
        var start = workflow.IndexOf($"\n  {job}:\n", StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"Job '{job}' not found in the workflow.");
        }

        var next = Regex.Match(workflow[(start + 1)..], @"\n  [a-z][a-z0-9\-]*:\n");
        var body = workflow[(start + 1)..];
        return next.Success && next.Index > job.Length ? body[..next.Index] : body;
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityWorkflow_DialectJobIsContainerOnlyValidatedAndLeastPrivilege()
    {
        // SEC-PAR-01, SEC-PAR-02 for the WP4 job
        var workflow = ReadWorkflow(NightlyWorkflow);
        var sqlite = JobBlock(workflow, "parity-sqlite");
        var dialects = JobBlock(workflow, "parity-dialects");

        sqlite.ShouldContain("FILTER=\"Category=Parity&Dialect!=Container\"");
        sqlite.ShouldNotContain("Dialect=Container");
        dialects.ShouldContain("FILTER=\"Category=Parity&Dialect=Container\"");
        dialects.ShouldNotContain("Dialect!=Container");

        foreach (var job in new[] { sqlite, dialects })
        {
            job.ShouldContain("FILTER=\"$FILTER&($PARITY_FILTER_INPUT)\"");
            job.ShouldContain("--filter \"$FILTER\"");
            job.ShouldContain("^[A-Za-z0-9_.~=\\!\\&\\|\\(\\)]*$");
            job.ShouldContain("^[0-9]{0,10}$");
            job.ShouldContain("permissions:\n      contents: read");
            job.ShouldContain("persist-credentials: false");
            job.IndexOf("Validate inputs", StringComparison.Ordinal).ShouldBeGreaterThan(-1);
            job.IndexOf("Validate inputs", StringComparison.Ordinal).ShouldBeLessThan(job.IndexOf("dotnet test", StringComparison.Ordinal));
            job.ShouldNotContain("secrets.");
            job.ShouldNotContain("eval ");
            JoinedLines(job).Where(l => l.TrimStart().StartsWith("run:", StringComparison.Ordinal) && l.Contains("${{", StringComparison.Ordinal)).ShouldBeEmpty();
        }

        dialects.ShouldContain("parity-baseline-dialects.json");
        dialects.ShouldContain("AUTHERIS_PARITY_RUN: '1'");
        dialects.ShouldContain("retention-days: 14");
        Regex.Count(workflow, @"^  parity-[a-z]+:\s*$", RegexOptions.Multiline).ShouldBe(2);
    }

    [Fact]
    public void ParityWorkflow_JobBlockFailsClosedAndSplitsJobs()
    {
        const string sample = "jobs:\n  one:\n    a: 1\n  two:\n    b: 2\n";
        JobBlock(sample, "one").ShouldContain("a: 1");
        JobBlock(sample, "one").ShouldNotContain("b: 2");
        JobBlock(sample, "two").ShouldContain("b: 2");
        Should.Throw<InvalidOperationException>(() => JobBlock(sample, "three"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParityProject_ContainerDialectsStayOutOfTheSqliteSelection()
    {
        // D-15: classes bound to a container host carry Dialect=Container, so the sqlite job filter excludes them
        var root = FindRepoRoot();
        var offenders = ParitySourceFiles(root)
            .Where(f => File.ReadAllText(f).Contains("ParityPostgresHostFixture host", StringComparison.Ordinal))
            .Where(f => !Path.GetFileName(f).StartsWith("ParityPostgresHostFixture", StringComparison.Ordinal))
            .Where(f => !File.ReadAllText(f).Contains("[Trait(\"Dialect\", \"Container\")]", StringComparison.Ordinal))
            .ToList();
        offenders.ShouldBeEmpty();
    }
}
