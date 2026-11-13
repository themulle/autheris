using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// WP-OI-CI-1 (#518) and SEC-CI-1-1..5: static assertions on the CI workflows and the change classifier. Workflows are read as text
/// (standard two-space layout), so no YAML dependency is needed.
/// </summary>
public sealed partial class CiWorkflowArchitectureTests
{
    private const string PullRequestOnly = "github.event_name == 'pull_request'";

    [GeneratedRegex(@"^  ([A-Za-z0-9_-]+):\s*(#.*)?$")]
    private static partial Regex JobHeader();

    [GeneratedRegex(@"^  pull_request(_target)?:", RegexOptions.Multiline)]
    private static partial Regex PullRequestTrigger();

    private static string Read(string workflow) => RepositoryScan.ReadText($".github/workflows/{workflow}");

    private static string Concurrency(string text)
    {
        var lines = text.Split('\n');
        int at = Array.FindIndex(lines, l => l.StartsWith("concurrency:", StringComparison.Ordinal));
        at.ShouldBeGreaterThanOrEqualTo(0, "workflow has no top-level concurrency block");
        return string.Join('\n', lines.Skip(at + 1).TakeWhile(l => l.StartsWith("  ", StringComparison.Ordinal)));
    }

    private static string[] Job(string workflow, string job)
    {
        var lines = Read(workflow).Split('\n');
        int start = Array.FindIndex(lines, l => JobHeader().IsMatch(l) && JobHeader().Match(l).Groups[1].Value == job);
        start.ShouldBeGreaterThanOrEqualTo(0, $"{workflow} has no job '{job}'");
        return lines.Skip(start + 1).TakeWhile(l => !JobHeader().IsMatch(l)).ToArray();
    }

    /// <summary>Job-level (four-space indented) <c>if:</c> value, or null.</summary>
    private static string? JobIf(string[] job) =>
        job.Where(l => l.StartsWith("    if:", StringComparison.Ordinal)).Select(l => l["    if:".Length..].Trim()).SingleOrDefault();

    [Fact]
    public void Main_runs_coalesce_on_ref_not_sha()
    {
        foreach (var workflow in new[] { "ci.yml", "codeql.yml", "trivy.yml" })
        {
            string c = Concurrency(Read(workflow));
            c.ShouldContain("github.ref", customMessage: workflow);
            c.ShouldNotContain("github.sha", customMessage: workflow);
            c.ShouldContain($"cancel-in-progress: ${{{{ {PullRequestOnly} }}}}", customMessage: workflow);
        }
    }

    [Fact]
    public void Every_pull_request_workflow_cancels_superseded_pr_runs()
    {
        foreach (var path in Directory.EnumerateFiles(Path.Combine(RepositoryScan.RepoRoot, ".github", "workflows"), "*.yml"))
        {
            string name = Path.GetFileName(path);
            string text = File.ReadAllText(path);
            if (!PullRequestTrigger().IsMatch(text)) continue;

            string c = Concurrency(text);
            c.ShouldContain("github.event.pull_request.number", customMessage: name);
            c.ShouldContain($"cancel-in-progress: ${{{{ {PullRequestOnly} }}}}", customMessage: $"{name} must cancel superseded PR runs and nothing else");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Cancel_in_progress_is_pull_request_only_for_publish_workflows()
    {
        foreach (var workflow in new[] { "docker-publish.yml", "release.yml" })
        {
            string text = Read(workflow);
            var cancel = Regex.Matches(text, @"^\s*cancel-in-progress:\s*(.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).ToList();
            foreach (var value in cancel)
            {
                (value == "false" || value == $"${{{{ {PullRequestOnly} }}}}").ShouldBeTrue($"{workflow}: cancel-in-progress '{value}' could cancel a tag, workflow_run or release run");
            }
        }

        Concurrency(Read("docker-publish.yml")).ShouldContain("github.run_id", customMessage: "tag, dispatch and workflow_run publish runs keep unique groups");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Sql_compiler_gate_has_no_path_condition()
    {
        string ci = Read("ci.yml");
        ci.ShouldNotContain("paths:", customMessage: "no workflow-level path filter in ci.yml");
        ci.ShouldNotContain("paths-ignore:");

        foreach (var name in new[] { "sql-compiler-gate", "helm-render" })
        {
            var job = Job("ci.yml", name);
            JobIf(job).ShouldBeNull($"{name} must stay unfiltered");
            job.ShouldNotContain(l => l.Contains("needs.changes", StringComparison.Ordinal) || l.StartsWith("    needs:", StringComparison.Ordinal) && l.Contains("changes", StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Security_suites_have_no_changes_gate()
    {
        // No job that runs Category=Security tests is skipped wholesale by the classifier; the job-level condition is only !cancelled()
        // (so a failed classifier means the full run). Doc-only changes narrow the filters but keep every Security test.
        foreach (var name in new[] { "build-and-test", "integration-tests", "spark-integration-tests" })
        {
            string? cond = JobIf(Job("ci.yml", name));
            cond.ShouldBe("${{ !cancelled() }}", $"{name} must not be gated by changes.dotnet");
        }

        string ci = Read("ci.yml");
        ci.ShouldContain("FILTER=\"($FILTER)&Category=Security\"");
        ci.ShouldContain("FILTER=\"Category=Spark&Category=Security\"");
        ci.ShouldContain("(FullyQualifiedName~Autheris.Tests.Architecture.|Category=Security)&$FILTER");

        // Only the full run is selected when the classifier output is anything but the literal 'false'.
        Regex.Count(ci, @"\[ ""\$DOTNET_CHANGED"" = ""false"" \]").ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Docs_only_filter_never_lists_src_tests_or_helm()
    {
        string script = RepositoryScan.ReadText(".github/scripts/ci-changes.py");
        var docOnly = Tuple(script, "DOC_ONLY");
        var deny = Tuple(script, "DENY");

        script.ShouldNotContain("event == \"push\"", customMessage: "only pull_request events may be narrowed; main pushes always run the full suites");
        docOnly.ShouldNotBeEmpty();
        foreach (var glob in docOnly)
        {
            glob.ShouldNotStartWith("src/");
            glob.ShouldNotStartWith("tests/");
            glob.ShouldNotContain("helm");
            glob.ShouldNotContain("csproj");
            glob.ShouldNotContain(".props");
            glob.ShouldNotContain(".targets");
            glob.ShouldNotContain(".sln");
            glob.ShouldNotBe("*");
            glob.ShouldNotBe("*/*");
            if (glob.StartsWith(".github", StringComparison.Ordinal)) glob.ShouldStartWith(".github/ISSUE_TEMPLATE/");
        }

        foreach (var required in new[] { "src/*", "tests/*", "deploy/helm/*", ".github/*", "*.csproj", "*.props", "*.targets", "*.sln", "global.json", "Directory.*", "nuget.config", "packages.lock.json" })
        {
            deny.ShouldContain(required);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Docs_only_filter_excludes_every_path_read_by_architecture_tests_or_architecture_job_is_unconditional()
    {
        // The classifier lists docs/, deploy/, benchmarks/ and *.md, which architecture tests read. This is safe only while the
        // architecture project runs on every PR: build-and-test has no changes gate and its narrowed filter keeps the whole project.
        var job = Job("ci.yml", "build-and-test");
        JobIf(job).ShouldBe("${{ !cancelled() }}");
        job.ShouldContain(l => l.Contains("FullyQualifiedName~Autheris.Tests.Architecture.|Category=Security", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Codeql_analyze_is_skipped_only_when_classifier_says_false()
    {
        var analyze = Job("codeql.yml", "analyze");
        JobIf(analyze).ShouldBe("${{ !cancelled() && needs.changes.outputs.dotnet != 'false' }}", "a fail-open gate: anything but the literal 'false' must analyze");
        analyze.Count(l => l.StartsWith("    needs:", StringComparison.Ordinal)).ShouldBe(1);
        analyze.ShouldContain("    needs: changes");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Changes_jobs_are_read_only_and_never_interpolate_event_data_into_scripts()
    {
        foreach (var workflow in new[] { "ci.yml", "codeql.yml" })
        {
            var job = Job(workflow, "changes");
            job.ShouldContain("      contents: read", customMessage: workflow);
            job.Count(l => l.Trim().StartsWith("contents:", StringComparison.Ordinal)).ShouldBe(1, workflow);
            job.ShouldContain("          fetch-depth: 0", customMessage: workflow);
            job.ShouldNotContain(l => l.Contains("write", StringComparison.Ordinal) && l.Contains(':'), workflow);
        }

        foreach (var path in Directory.EnumerateFiles(Path.Combine(RepositoryScan.RepoRoot, ".github", "workflows"), "*.yml"))
        {
            foreach (var line in File.ReadAllLines(path).Where(l => l.TrimStart().StartsWith("run:", StringComparison.Ordinal)))
            {
                line.ShouldNotContain("${{ needs.changes", customMessage: path);
                line.ShouldNotContain("${{ github.event.pull_request", customMessage: path);
            }
        }
    }

    private static List<string> Tuple(string script, string name)
    {
        var match = Regex.Match(script, name + @" = \((?<body>.*?)\n?\)\s*\n", RegexOptions.Singleline);
        match.Success.ShouldBeTrue($"{name} not found in ci-changes.py");
        return Regex.Matches(match.Groups["body"].Value, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
    }
}
