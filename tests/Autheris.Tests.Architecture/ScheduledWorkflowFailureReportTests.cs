using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// A failing scheduled workflow is never silent: every workflow with a <c>schedule</c> trigger ends with one <c>report-failure</c> job
/// that opens or updates one issue per workflow. The job runs only for failed scheduled runs, holds the only <c>issues: write</c>
/// permission in the repository (job-scoped), uses the job token only through <c>GH_TOKEN</c>, interpolates no event data into
/// <c>run:</c> and posts nothing but the run URL and the failed job ids. Each scanner has negative self-checks on in-memory samples.
/// </summary>
public sealed class ScheduledWorkflowFailureReportTests
{
    internal const string ReportJob = "report-failure";
    internal const string CanonicalIf = "failure() && github.event_name == 'schedule' && github.event.repository.fork == false";
    internal const string ScriptPath = ".github/scripts/report-scheduled-failure.sh";
    private const string RunLine = "run: bash " + ScriptPath;

    /// <summary>footprint-budget.yml is also a reusable workflow called by release.yml, whose caller job grants no issues permission:
    /// a nested job requesting <c>issues: write</c> would invalidate the release workflow.</summary>
    private static readonly string[] Exempt = ["footprint-budget.yml"];

    private static readonly string[] AllowedExpressionLines = ["GH_TOKEN: ${{ github.token }}", "NEEDS_JSON: ${{ toJSON(needs) }}"];

    // ---------- scanners ----------

    /// <summary>The workflow text without its <c>report-failure</c> job, for the per-workflow least-privilege guards.</summary>
    internal static string WithoutReportFailureJob(string workflow) =>
        SimulationIsolationArchitectureTests.Jobs(workflow).TryGetValue(ReportJob, out var block)
            ? workflow.Replace(block, string.Empty, StringComparison.Ordinal)
            : workflow;

    private static bool IsScheduled(string workflow) => ParityIsolationArchitectureTests.TriggerKeys(workflow).Contains("schedule");

    private static IReadOnlyList<string> Children(IReadOnlyList<string> lines, string key)
    {
        var idx = lines.ToList().FindIndex(l => l == $"    {key}:");
        return idx < 0 ? [] : lines.Skip(idx + 1).TakeWhile(l => l.StartsWith("      ", StringComparison.Ordinal)).Select(l => l.Trim()).ToList();
    }

    private static IReadOnlyList<string> Scalars(IReadOnlyList<string> lines, string key) =>
        lines.Where(l => l.StartsWith($"    {key}:", StringComparison.Ordinal)).Select(l => l[(key.Length + 5)..].Trim()).ToList();

    /// <summary>Jobs bound to a GitHub environment (required reviewers): they can wait for approval indefinitely.</summary>
    internal static IReadOnlyList<string> ApprovalGatedJobs(string workflow) =>
        SimulationIsolationArchitectureTests.Jobs(workflow).Where(j => Regex.IsMatch(j.Value, @"(?m)^    environment:")).Select(j => j.Key).ToList();

    private static string RunsOn(string jobBlock) =>
        Regex.Match(jobBlock, @"(?m)^    runs-on:\s*(?<value>[^#\r\n]*?)\s*(#.*)?$").Groups["value"].Value;

    internal static IReadOnlyList<string> ReportJobViolations(string workflow)
    {
        var jobs = SimulationIsolationArchitectureTests.Jobs(workflow);
        if (!jobs.TryGetValue(ReportJob, out var block))
        {
            return [$"{ReportJob} job missing"];
        }

        var bad = new List<string>();
        var lines = block.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        if (!Scalars(lines, "if").SequenceEqual([CanonicalIf]))
        {
            bad.Add($"if must be exactly '{CanonicalIf}'");
        }

        var needs = Scalars(lines, "needs");
        var gated = ApprovalGatedJobs(workflow);
        var expected = jobs.Keys.Where(k => k != ReportJob && !gated.Contains(k)).Order(StringComparer.Ordinal).ToList();
        var actual = needs.Count == 1 && needs[0].StartsWith('[') && needs[0].EndsWith(']')
            ? needs[0][1..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToList()
            : [];
        if (!actual.SequenceEqual(expected))
        {
            bad.Add($"needs must list every other job that is not approval-gated: [{string.Join(", ", expected)}]");
        }

        var timeouts = Scalars(lines, "timeout-minutes");
        if (timeouts.Count != 1 || !int.TryParse(timeouts[0], out var minutes) || minutes is < 1 or > 15)
        {
            bad.Add("timeout-minutes must be set (1..15)");
        }

        if (block.Contains("continue-on-error", StringComparison.Ordinal))
        {
            bad.Add("continue-on-error is not allowed");
        }

        var runners = jobs.Where(j => j.Key != ReportJob).Select(j => RunsOn(j.Value)).ToHashSet(StringComparer.Ordinal);
        if (!runners.Contains(RunsOn(block)))
        {
            bad.Add("runs-on must be a runner the other jobs of the workflow use");
        }

        if (!Children(lines, "permissions").Order(StringComparer.Ordinal).SequenceEqual(["contents: read", "issues: write"]))
        {
            bad.Add("permissions must be exactly 'contents: read' and 'issues: write'");
        }

        if (block.Contains("secrets", StringComparison.OrdinalIgnoreCase) || block.Contains("GITHUB_TOKEN", StringComparison.Ordinal))
        {
            bad.Add("no secrets: the job token is passed only as GH_TOKEN: ${{ github.token }}");
        }

        bad.AddRange(lines.Where(l => l.Contains("${{", StringComparison.Ordinal) && !AllowedExpressionLines.Contains(l.Trim()))
            .Select(l => $"expression outside the allowed env lines: {l.Trim()}"));

        var runs = lines.Where(l => Regex.IsMatch(l, @"^\s*(-\s+)?run:")).Select(l => Regex.Replace(l.Trim(), @"^-\s+", string.Empty)).ToList();
        if (!runs.SequenceEqual([RunLine]))
        {
            bad.Add($"the only run line must be '{RunLine}'");
        }

        var uses = lines.Select(l => Regex.Match(l, @"\buses:\s*(?<action>[^@\s]+)@(?<ref>\S+)")).Where(m => m.Success).ToList();
        if (uses.Count != 1 || uses[0].Groups["action"].Value != "actions/checkout" || !Regex.IsMatch(uses[0].Groups["ref"].Value, "^[0-9a-f]{40}$"))
        {
            bad.Add("the only action must be actions/checkout pinned by a 40-hex SHA");
        }

        if (!lines.Any(l => l.Trim() == "persist-credentials: false"))
        {
            bad.Add("checkout must set persist-credentials: false");
        }

        return bad;
    }

    /// <summary>Any <c>issues: write</c> or <c>write-all</c> outside the report job (top-level or another job).</summary>
    internal static IReadOnlyList<string> IssueWriteOutsideReportJob(string workflow) =>
        Regex.Matches(WithoutReportFailureJob(workflow), @"issues:\s*write|write-all").Select(m => m.Value).ToList();

    internal static IReadOnlyList<string> ScriptViolations(string script)
    {
        var bad = new List<string>();
        if (!script.Contains("set -euo pipefail", StringComparison.Ordinal)) bad.Add("set -euo pipefail missing");
        if (Regex.IsMatch(script, @"set\s+-[a-wyz]*x|set\s+-o\s+xtrace|printenv|\benv\b\s*\|")) bad.Add("tracing or environment dump");
        if (script.Contains("secrets", StringComparison.OrdinalIgnoreCase) || script.Contains("${{", StringComparison.Ordinal)) bad.Add("secrets or expressions in the script");
        if (!script.Contains("label=\"ci-nightly-failure\"", StringComparison.Ordinal)) bad.Add("label ci-nightly-failure missing");
        if (!script.Contains("title=\"Scheduled workflow failed: ${GITHUB_WORKFLOW}\"", StringComparison.Ordinal)) bad.Add("title missing");
        if (script.Contains("|| true", StringComparison.Ordinal)) bad.Add("no blanket error suppression");
        if (!script.Contains("label_error=\"$(gh label create ", StringComparison.Ordinal) || !script.Contains("*\"already exists\"*)", StringComparison.Ordinal)
            || !Regex.IsMatch(script, @"(?m)^\s*\*\) echo ""::warning::[^$""]*"" ;;"))
        {
            bad.Add("label creation may only tolerate an existing label and must warn (without echoing the error) otherwise");
        }

        if (!script.Contains("gh issue view ", StringComparison.Ordinal)) bad.Add("the duplicate-comment check is missing");

        var bodies = Regex.Matches(script, "--body\\s+(?<arg>\\S+)").Select(m => m.Groups["arg"].Value).ToList();
        if (bodies.Count == 0 || bodies.Any(a => a != "\"$body\"")) bad.Add("every --body argument must be \"$body\"");

        var assignments = Regex.Matches(script, @"(?m)^body=(?<value>.*)$").Select(m => m.Groups["value"].Value).ToList();
        var refs = assignments.SelectMany(v => Regex.Matches(v, @"\$\{?(?<name>[A-Za-z_][A-Za-z0-9_]*)").Select(m => m.Groups["name"].Value)).Distinct().Order(StringComparer.Ordinal).ToList();
        if (assignments.Count != 1 || !refs.SequenceEqual(["failed_jobs", "run_url"])) bad.Add("the body may reference only $run_url and $failed_jobs");

        return bad;
    }

    // ---------- tests ----------

    [Fact]
    [Trait("Category", "Security")]
    public void Every_scheduled_workflow_reports_failures_through_the_canonical_job()
    {
        var workflows = ParityIsolationArchitectureTests.ReadWorkflows(ParityIsolationArchitectureTests.FindRepoRoot());
        var scheduled = workflows.Where(w => IsScheduled(w.Value)).ToDictionary(w => w.Key, w => w.Value);
        scheduled.ShouldNotBeEmpty("no scheduled workflow found: vacuous guard");

        foreach (var name in Exempt)
        {
            scheduled.ShouldContainKey(name, $"stale exemption: {name} is no longer scheduled");
            ParityIsolationArchitectureTests.TriggerKeys(scheduled[name]).ShouldContain("workflow_call", $"stale exemption: {name} is no longer a reusable workflow");
            SimulationIsolationArchitectureTests.Jobs(scheduled[name]).ShouldNotContainKey(ReportJob);
        }

        scheduled.Where(w => !Exempt.Contains(w.Key)).SelectMany(w => ReportJobViolations(w.Value).Select(v => $"{w.Key}: {v}")).ShouldBeEmpty();

        // SEC-CI-1: approval-gated jobs never finish without a reviewer, so report-failure must not wait for them. The exemption is pinned
        // to the one known job (the G9 Databricks job fails by design until a live Databricks test exists and would need its own alert);
        // it expires when the environment binding is removed, because the job then has to be in needs again.
        scheduled.Where(w => !Exempt.Contains(w.Key)).SelectMany(w => ApprovalGatedJobs(w.Value).Select(j => $"{w.Key}/{j}"))
            .ShouldBe(["sql-compiler-gate-nightly.yml/sql-compiler-gate-databricks-g9"]);
        workflows.Where(w => !scheduled.ContainsKey(w.Key) && SimulationIsolationArchitectureTests.Jobs(w.Value).ContainsKey(ReportJob)).Select(w => w.Key)
            .ShouldBeEmpty("only scheduled workflows carry a report-failure job");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue_write_is_granted_only_to_the_report_job()
    {
        var workflows = ParityIsolationArchitectureTests.ReadWorkflows(ParityIsolationArchitectureTests.FindRepoRoot());
        workflows.SelectMany(w => IssueWriteOutsideReportJob(w.Value).Select(v => $"{w.Key}: {v}")).ShouldBeEmpty();

        // negative self-checks
        IssueWriteOutsideReportJob("permissions: write-all\njobs:\n  a:\n    runs-on: x\n").Count.ShouldBe(1);
        IssueWriteOutsideReportJob("jobs:\n  a:\n    permissions:\n      issues: write\n  report-failure:\n    permissions:\n      issues: write\n").Count.ShouldBe(1);
        IssueWriteOutsideReportJob("jobs:\n  a:\n    permissions:\n      contents: read\n  report-failure:\n    permissions:\n      issues: write\n").ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Report_script_posts_only_the_run_url_and_failed_job_ids()
    {
        ScriptViolations(RepositoryScan.ReadText(ScriptPath)).ShouldBeEmpty();

        // negative self-checks
        const string Good = "set -euo pipefail\nlabel=\"ci-nightly-failure\"\ntitle=\"Scheduled workflow failed: ${GITHUB_WORKFLOW}\"\n"
                            + "body=\"$(printf 'x %s %s' \"$run_url\" \"$failed_jobs\")\"\n"
                            + "if ! label_error=\"$(gh label create \"$label\" 2>&1 >/dev/null)\"; then\n  case \"$label_error\" in\n    *\"already exists\"*) ;;\n"
                            + "    *) echo \"::warning::Could not create the label.\" ;;\n  esac\nfi\nlast=\"$(gh issue view 1 --json comments)\"\ngh issue create --body \"$body\"\n";
        ScriptViolations(Good).ShouldBeEmpty();
        ScriptViolations(Good.Replace("set -euo pipefail", "set -euxo pipefail", StringComparison.Ordinal)).ShouldNotBeEmpty();
        ScriptViolations(Good + "printenv\n").ShouldNotBeEmpty();
        ScriptViolations(Good.Replace("\"$failed_jobs\"", "\"$failed_jobs\" \"$GH_TOKEN\"", StringComparison.Ordinal)).ShouldNotBeEmpty();
        ScriptViolations(Good.Replace("\"$failed_jobs\"", "\"$(cat log.txt)\"", StringComparison.Ordinal)).ShouldNotBeEmpty();
        ScriptViolations(Good + "gh issue comment 1 --body \"$(cat log.txt)\"\n").ShouldNotBeEmpty();
        ScriptViolations(Good + "echo ${{ secrets.X }}\n").ShouldNotBeEmpty();
        ScriptViolations(Good + "gh label create \"$label\" || true\n").ShouldNotBeEmpty();
        ScriptViolations(Good.Replace("*\"already exists\"*) ;;", "*) ;;", StringComparison.Ordinal)).ShouldNotBeEmpty();
        ScriptViolations(Good.Replace("Could not create the label.", "$label_error", StringComparison.Ordinal)).ShouldNotBeEmpty();
        ScriptViolations(Good.Replace("gh issue view 1", "true", StringComparison.Ordinal)).ShouldNotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Report_job_scanner_rejects_unsafe_variants()
    {
        const string Good =
            "on:\n  schedule:\n    - cron: '0 2 * * *'\njobs:\n  a:\n    runs-on: ubuntu-latest\n  b:\n    runs-on: ubuntu-latest # note\n"
            + "  g:\n    runs-on: ubuntu-latest\n    environment: gated\n"
            + "  report-failure:\n    name: Report scheduled failure\n    needs: [a, b]\n"
            + "    if: failure() && github.event_name == 'schedule' && github.event.repository.fork == false\n"
            + "    runs-on: ubuntu-latest\n    timeout-minutes: 5\n    permissions:\n      contents: read\n      issues: write\n    steps:\n"
            + "      - name: Checkout repository\n        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1\n"
            + "        with:\n          persist-credentials: false\n          sparse-checkout: .github/scripts\n"
            + "      - name: Open or update the failure issue\n        env:\n          GH_TOKEN: ${{ github.token }}\n"
            + "          NEEDS_JSON: ${{ toJSON(needs) }}\n        run: bash .github/scripts/report-scheduled-failure.sh\n";
        ReportJobViolations(Good).ShouldBeEmpty();
        WithoutReportFailureJob(Good).ShouldNotContain(ReportJob);
        WithoutReportFailureJob(Good).ShouldContain("  g:\n    runs-on: ubuntu-latest\n    environment: gated\n");
        ApprovalGatedJobs(Good).ShouldBe(["g"]);

        string R(string from, string to) => Good.Replace(from, to, StringComparison.Ordinal);
        string[] rejected =
        [
            R(" && github.event_name == 'schedule'", string.Empty),
            R("if: failure() && github.event_name == 'schedule' && github.event.repository.fork == false", "if: always()"),
            R("      issues: write\n", "      issues: write\n      actions: write\n"),
            R("      contents: read\n", "      contents: write\n"),
            R("needs: [a, b]", "needs: [a]"),
            R("needs: [a, b]", "needs: [a, b, g]"),
            R("    timeout-minutes: 5\n", string.Empty),
            R("    timeout-minutes: 5\n", "    timeout-minutes: 5\n    continue-on-error: true\n"),
            R("    runs-on: ubuntu-latest\n    timeout-minutes", "    runs-on: self-hosted\n    timeout-minutes"),
            R("run: bash .github/scripts/report-scheduled-failure.sh", "run: echo \"${{ github.event.head_commit.message }}\""),
            R("run: bash .github/scripts/report-scheduled-failure.sh", "run: bash .github/scripts/report-scheduled-failure.sh\n        run: gh issue create --body \"$LOG\""),
            R("GH_TOKEN: ${{ github.token }}", "GH_TOKEN: ${{ secrets.PAT }}"),
            R("          NEEDS_JSON", "          MSG: ${{ github.event.issue.title }}\n          NEEDS_JSON"),
            R("@3d3c42e5aac5ba805825da76410c181273ba90b1", "@v7"),
            R("        uses: actions/checkout", "        uses: some/action@3d3c42e5aac5ba805825da76410c181273ba90b1\n      - uses: actions/checkout"),
            R("persist-credentials: false", "persist-credentials: true"),
        ];
        foreach (var sample in rejected)
        {
            ReportJobViolations(sample).ShouldNotBeEmpty(sample);
        }

        ReportJobViolations("on:\n  schedule:\n    - cron: '0 2 * * *'\njobs:\n  a:\n    runs-on: x\n").ShouldBe([$"{ReportJob} job missing"]);
    }
}
