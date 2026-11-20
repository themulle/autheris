using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        // No job that runs Category=Security tests is skipped wholesale by changes.dotnet; the job-level condition on build-and-test
        // and integration-tests is only !cancelled() (so a failed classifier means the full run). Doc-only changes narrow the filters
        // but keep every Security test.
        foreach (var name in new[] { "build-and-test", "integration-tests" })
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

    [Fact]
    public void Integration_tests_run_across_six_balanced_shards()
    {
        var job = string.Join('\n', Job("ci.yml", "integration-tests"));
        job.ShouldContain("name: Integration Tests (shard ${{ matrix.shard }}/6)");
        job.ShouldContain("shard: [1, 2, 3, 4, 5, 6]");
        job.ShouldContain("python3 .github/scripts/integration-shard-filter.py \"$((SHARD - 1))\" 6");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Security_tests_are_never_excluded_by_the_pr_filter()
    {
        // WP-OI-CI-5 (#522) & SEC-OI-522-1/2: BASE_FILTER keeps all non-Spark/Parity Security tests in PR shards, even when tagged Oracle or Stress.
        string baseFilter = ReadBaseFilter();
        baseFilter.ShouldBe("Category!=Spark&Category!=Parity&((Category!=Oracle&Category!=Stress)|Category=Security)");

        EvaluateCategoryFilter(baseFilter, new HashSet<string> { "Security" }).ShouldBeTrue();
        EvaluateCategoryFilter(baseFilter, new HashSet<string> { "Security", "Oracle" }).ShouldBeTrue();
        EvaluateCategoryFilter(baseFilter, new HashSet<string> { "Security", "Stress" }).ShouldBeTrue();
        EvaluateCategoryFilter(baseFilter, new HashSet<string> { "Security", "Oracle", "Stress" }).ShouldBeTrue();
        EvaluateCategoryFilter(baseFilter, new HashSet<string> { "Oracle" }).ShouldBeFalse();
        EvaluateCategoryFilter(baseFilter, new HashSet<string> { "Stress" }).ShouldBeFalse();
        EvaluateCategoryFilter(baseFilter, new HashSet<string> { "Spark" }).ShouldBeFalse();
        EvaluateCategoryFilter(baseFilter, new HashSet<string> { "Parity" }).ShouldBeFalse();

        var (classes, methods) = ScanIntegrationTraits();
        var allowedSparkSecurity = LoadSecurityNightlyCombinationsBaseline();

        var securityClasses = classes.Where(c => c.Value.Contains("Security")).ToList();
        securityClasses.Count.ShouldBeGreaterThan(50, "expected integration classes with Category=Security");

        var excludedClasses = securityClasses
            .Where(c => !EvaluateCategoryFilter(baseFilter, c.Value) && !allowedSparkSecurity.ContainsKey(c.Key))
            .Select(c => $"{c.Key} ({string.Join(",", c.Value)})")
            .ToList();
        excludedClasses.ShouldBeEmpty($"Security-tagged integration classes excluded by BASE_FILTER: {string.Join("; ", excludedClasses)}");

        // Every Security-tagged method (class-level or method-level, theories included) must be selected by a PR lane (BASE_FILTER or spark-integration-tests).
        var securityMethods = methods.Where(m => m.Value.Contains("Security")).ToList();
        securityMethods.Count.ShouldBeGreaterThan(100, "expected integration test methods with Category=Security");

        var unselectedMethods = securityMethods
            .Where(m => !EvaluateCategoryFilter(baseFilter, m.Value) && !EvaluateCategoryFilter("Category=Spark&Category=Security", m.Value))
            .Select(m => $"{m.Key} ({string.Join(",", m.Value)})")
            .ToList();
        unselectedMethods.ShouldBeEmpty($"Security-tagged integration test methods not selected by any PR filter: {string.Join("; ", unselectedMethods)}");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Method_level_security_traits_are_selected_by_a_pr_job()
    {
        // SEC-OI-522-1: method-level [Trait("Category", "Security")] (including [Theory] and inherited base-class methods) must be selected by a PR job.
        string baseFilter = ReadBaseFilter();
        var (classes, methods) = ScanIntegrationTraits();

        var methodLevelOnly = methods
            .Where(m => m.Value.Contains("Security"))
            .Where(m =>
            {
                int lastDot = m.Key.LastIndexOf('.');
                string className = m.Key[..lastDot];
                return !classes.TryGetValue(className, out var classCats) || !classCats.Contains("Security");
            })
            .ToList();

        methodLevelOnly.ShouldNotBeEmpty("expected integration classes with method-level [Trait(\"Category\", \"Security\")]");
        foreach (var method in methodLevelOnly)
        {
            EvaluateCategoryFilter(baseFilter, method.Value).ShouldBeTrue($"{method.Key} with categories [{string.Join(", ", method.Value)}] was excluded by BASE_FILTER");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void No_security_class_is_only_in_a_nightly_category()
    {
        // WP-OI-CI-5 (#522): verify Oracle and Stress traits are applied to the planned classes, and no Security class is routed only to nightly.
        var (classes, _) = ScanIntegrationTraits();
        string[] expectedOracle =
        [
            "Autheris.Tests.Integration.OracleGatewayPathTests",
            "Autheris.Tests.Integration.OracleGovernedEndToEndTests",
            "Autheris.Tests.Integration.AstCompilerOracleExecutionTests",
            "Autheris.Tests.Integration.AstCompilerOracleDmlExecutionTests",
            "Autheris.Tests.Integration.OracleIntegrationTests",
        ];
        foreach (var oracleClass in expectedOracle)
        {
            classes.ShouldContainKey(oracleClass);
            classes[oracleClass].ShouldContain("Oracle", $"{oracleClass} must carry [Trait(\"Category\", \"Oracle\")]");
        }

        classes.ShouldContainKey("Autheris.Tests.Integration.QueryDefenseStressTests");
        classes["Autheris.Tests.Integration.QueryDefenseStressTests"].ShouldContain("Stress");

        string[] nightlyCategories = ["Oracle", "Stress", "Spark", "Parity", "NightlyGate", "Metamorphic", "Fuzz", "Mutant"];
        foreach (var (className, cats) in classes)
        {
            if (className.Contains("Security", StringComparison.Ordinal) && cats.Any(nightlyCategories.Contains))
            {
                cats.ShouldContain("Security", $"{className} is named *Security* and carries a nightly category ({string.Join(", ", cats)}) without Category=Security");
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Security_spark_oracle_stress_combinations_do_not_grow()
    {
        // SEC-OI-522-1 / Q-SEC8-1: shrink-only baseline of classes combining Category=Security with Spark/Parity/Oracle/Stress.
        var (classes, methods) = ScanIntegrationTraits();
        var baseline = LoadSecurityNightlyCombinationsBaseline();
        string[] gatedCategories = ["Spark", "Parity", "Oracle", "Stress", "NightlyGate", "Metamorphic", "Fuzz", "Mutant"];

        var actualCombinations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (className, cats) in classes)
        {
            if (!cats.Contains("Security")) continue;
            var gated = cats.Where(gatedCategories.Contains).Order(StringComparer.Ordinal).ToList();
            if (gated.Count > 0)
            {
                actualCombinations[className] = string.Join(",", gated);
            }
        }

        foreach (var (methodName, cats) in methods)
        {
            if (!cats.Contains("Security")) continue;
            int lastDot = methodName.LastIndexOf('.');
            string className = methodName[..lastDot];
            var gated = cats.Where(gatedCategories.Contains).Order(StringComparer.Ordinal).ToList();
            if (gated.Count > 0 && !actualCombinations.ContainsKey(className))
            {
                actualCombinations[className] = string.Join(",", gated);
            }
        }

        var unapproved = actualCombinations
            .Where(kv => !baseline.TryGetValue(kv.Key, out var allowed) || allowed != kv.Value)
            .Select(kv => $"{kv.Key}|{kv.Value}")
            .ToList();
        unapproved.ShouldBeEmpty(
            "SEC-OI-522-1: New combination of Category=Security with Spark/Parity/Oracle/Stress detected. " +
            $"Either drop the extra category or add an unconditional PR lane: {string.Join(", ", unapproved)}");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Oracle_and_spark_pr_jobs_run_when_changes_unknown()
    {
        // WP-OI-CI-5 (#522) & SEC-OI-522-3: oracle-smoke and spark-integration-tests only skip when changes output is literally 'false'.
        var changesJob = string.Join('\n', Job("ci.yml", "changes"));
        changesJob.ShouldContain("dotnet: ${{ steps.classify.outputs.dotnet }}");
        changesJob.ShouldContain("oracle: ${{ steps.classify.outputs.oracle }}");
        changesJob.ShouldContain("spark: ${{ steps.classify.outputs.spark }}");

        var oracleJob = Job("ci.yml", "oracle-smoke");
        JobIf(oracleJob).ShouldBe("${{ !cancelled() && needs.changes.outputs.oracle != 'false' }}");
        string.Join('\n', oracleJob).ShouldContain("--filter \"Category=Oracle\"");

        var sparkJob = Job("ci.yml", "spark-integration-tests");
        JobIf(sparkJob).ShouldBe("${{ !cancelled() && needs.changes.outputs.spark != 'false' }}");

        string script = RepositoryScan.ReadText(".github/scripts/ci-changes.py");
        var oraclePatterns = Tuple(script, "ORACLE_PATTERNS");
        var sparkPatterns = Tuple(script, "SPARK_PATTERNS");
        oraclePatterns.ShouldContain("**/*Oracle*");
        sparkPatterns.ShouldContain("**/*Spark*");
        sparkPatterns.ShouldContain("**/*Databricks*");
        script.ShouldContain("return {\"dotnet\": True, \"oracle\": True, \"spark\": True}");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Integration_nightly_uploads_trx_and_reports_failures()
    {
        string nightly = Read("integration-nightly.yml");
        nightly.ShouldContain("cron: '0 1 * * *'");
        nightly.ShouldContain("workflow_dispatch:");

        var testJob = string.Join('\n', Job("integration-nightly.yml", "integration-nightly"));
        testJob.ShouldContain("timeout-minutes: 90");
        testJob.ShouldContain("--filter \"Category=Oracle|Category=Stress|Category=Spark\"");
        testJob.ShouldContain("--logger \"trx\"");
        testJob.ShouldContain("--blame-hang-timeout 5m");
        testJob.ShouldContain("name: test-results-integration-nightly");
        testJob.ShouldContain("retention-days: 7");

        ScheduledWorkflowFailureReportTests.ReportJobViolations(nightly).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Nightly_alert_job_is_the_only_issues_write_scope()
    {
        // SEC-OI-522-4/5: top-level permissions are {}, test job has contents: read only, report-failure holds the only issues: write scope.
        string nightly = Read("integration-nightly.yml");
        nightly.ShouldContain("permissions: {}");
        ScheduledWorkflowFailureReportTests.IssueWriteOutsideReportJob(nightly).ShouldBeEmpty();

        var testJob = Job("integration-nightly.yml", "integration-nightly");
        testJob.ShouldContain("      contents: read");
        testJob.ShouldNotContain(l => l.Contains("write", StringComparison.Ordinal));
        nightly.ShouldNotContain("secrets.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Nightly_has_no_pull_request_trigger_and_is_fork_guarded()
    {
        // SEC-OI-522-4: schedule and workflow_dispatch only; fork guard on both jobs.
        string nightly = Read("integration-nightly.yml");
        ParityIsolationArchitectureTests.TriggerKeys(nightly).ShouldBe(["schedule", "workflow_dispatch"], ignoreOrder: true);
        PullRequestTrigger().IsMatch(nightly).ShouldBeFalse();
        nightly.ShouldNotContain("workflow_run:");
        nightly.ShouldNotContain("issue_comment:");

        JobIf(Job("integration-nightly.yml", "integration-nightly")).ShouldBe("github.event.repository.fork == false");
        JobIf(Job("integration-nightly.yml", "report-failure")).ShouldBe(ScheduledWorkflowFailureReportTests.CanonicalIf);
    }

    private const string G9Workflow = "sql-compiler-gate-nightly.yml";
    private const string G9JobName = "sql-compiler-gate-databricks-g9";
    private const string G9TestStep = "Live Databricks suites";

    /// <summary>Splits a job body into its job-level lines and its steps (a step starts at a six-space <c>- </c> item).</summary>
    private static (string[] JobLevel, List<string[]> Steps) SplitSteps(string[] job)
    {
        int firstStep = Array.FindIndex(job, l => l.StartsWith("      - ", StringComparison.Ordinal));
        firstStep.ShouldBeGreaterThanOrEqualTo(0, "job has no steps");
        var steps = new List<string[]>();
        var current = new List<string>();
        foreach (var line in job.Skip(firstStep))
        {
            if (line.StartsWith("      - ", StringComparison.Ordinal) && current.Count > 0)
            {
                steps.Add(current.ToArray());
                current = [];
            }

            current.Add(line);
        }

        steps.Add(current.ToArray());
        return (job.Take(firstStep).ToArray(), steps);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Databricks_secrets_only_in_the_test_step_env()
    {
        // SEC-OI-609-1/2: the Databricks secrets are declared only in the step-level env of the single live-test step.
        // Checkout, setup and restore see none, and no workflow forwards all secrets.
        foreach (var path in Directory.EnumerateFiles(Path.Combine(RepositoryScan.RepoRoot, ".github", "workflows"), "*.yml"))
        {
            string name = Path.GetFileName(path);
            string text = File.ReadAllText(path);
            text.ShouldNotContain("secrets: inherit", customMessage: name);
            text.ShouldNotContain("toJSON(secrets)", customMessage: name);
            if (name != G9Workflow)
            {
                text.ShouldNotContain("secrets.DATABRICKS_", customMessage: $"{name} must not read Databricks secrets");
            }
        }

        var job = Job(G9Workflow, G9JobName);
        job.ShouldContain(l => l.StartsWith("    environment: databricks-g9", StringComparison.Ordinal), "G9 stays bound to the protected environment");
        var (jobLevel, steps) = SplitSteps(job);
        jobLevel.ShouldNotContain(l => l.Contains("secrets.", StringComparison.Ordinal), "no job-level env or other job-level secret reference");

        var withSecrets = steps.Where(step => step.Any(l => l.Contains("secrets.DATABRICKS_", StringComparison.Ordinal))).ToList();
        withSecrets.Count.ShouldBe(1, "exactly one step may reference the Databricks secrets");
        var testStep = withSecrets[0];
        testStep[0].ShouldContain(G9TestStep);

        int envAt = Array.FindIndex(testStep, l => l == "        env:");
        int runAt = Array.FindIndex(testStep, l => l == "        run: |");
        envAt.ShouldBeGreaterThan(0, "the live-test step declares its secrets in a step-level env block");
        runAt.ShouldBeGreaterThan(envAt, "the secrets are declared before the run block");
        for (int i = 0; i < testStep.Length; i++)
        {
            if (testStep[i].Contains("secrets.DATABRICKS_", StringComparison.Ordinal))
            {
                (i > envAt && i < runAt).ShouldBeTrue($"secret reference outside the step env block: {testStep[i].Trim()}");
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void G9_job_is_main_only()
    {
        // SEC-OI-609-1: a manual run of a branch other than main never reaches the secrets.
        JobIf(Job(G9Workflow, G9JobName)).ShouldNotBeNull().ShouldContain("github.ref == 'refs/heads/main'");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void G9_job_uploads_no_artifacts()
    {
        // SEC-OI-609-3: the live log stays in RUNNER_TEMP, is never uploaded and never carries detailed verbosity.
        string job = string.Join('\n', Job(G9Workflow, G9JobName));
        job.ShouldNotContain("upload-artifact");
        job.ShouldNotContain("--verbosity detailed");
        job.ShouldContain("--verbosity normal");
        Regex.Count(job, @"g9\.log").ShouldBeGreaterThan(0);
        foreach (Match match in Regex.Matches(job, @"\S*g9\.log"))
        {
            match.Value.ShouldStartWith("\"$RUNNER_TEMP/", Case.Sensitive, "g9.log must be written under RUNNER_TEMP");
        }
    }

    [Fact]
    public void Every_ci_job_has_a_timeout()
    {
        // WP-OI-CI-6 (#523): keeps the #517 guarantee that no ci.yml job can hang for the 6 h default.
        var lines = Read("ci.yml").Split('\n');
        int jobsAt = Array.FindIndex(lines, l => l.TrimEnd() == "jobs:");
        jobsAt.ShouldBeGreaterThanOrEqualTo(0);
        var jobs = lines.Skip(jobsAt + 1).TakeWhile(l => l.Length == 0 || l[0] == ' ' || l[0] == '#').ToArray();
        var names = jobs.Where(l => JobHeader().IsMatch(l)).Select(l => JobHeader().Match(l).Groups[1].Value).ToList();
        names.ShouldNotBeEmpty();
        foreach (var name in names)
        {
            Job("ci.yml", name).ShouldContain(l => Regex.IsMatch(l, @"^    timeout-minutes:\s*\d+"), $"ci.yml job '{name}' must declare timeout-minutes");
        }
    }

    private static string ReadBaseFilter()
    {
        string script = RepositoryScan.ReadText(".github/scripts/integration-shard-filter.py");
        var match = Regex.Match(script, @"^BASE_FILTER\s*=\s*""(?<filter>[^""]+)""", RegexOptions.Multiline);
        match.Success.ShouldBeTrue("BASE_FILTER not found in .github/scripts/integration-shard-filter.py");
        return match.Groups["filter"].Value;
    }

    private static Dictionary<string, string> LoadSecurityNightlyCombinationsBaseline()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in RepositoryScan.ReadBaseline("security-nightly-category-combinations.txt"))
        {
            var parts = line.Split('|');
            parts.Length.ShouldBe(2, $"Invalid line in security-nightly-category-combinations.txt: {line}");
            result[parts[0].Trim()] = parts[1].Trim();
        }

        return result;
    }

    internal static bool EvaluateCategoryFilter(string expr, IReadOnlySet<string> categories)
    {
        var tokens = Regex.Matches(expr, @"\(|\)|&|\||[^()&|\s]+").Select(m => m.Value).ToList();
        int pos = 0;
        bool val = EvalOr(tokens, ref pos, categories);
        pos.ShouldBe(tokens.Count, $"Unconsumed filter tokens starting at {pos} in '{expr}'");
        return val;

        static bool EvalOr(List<string> t, ref int p, IReadOnlySet<string> cats)
        {
            bool lhs = EvalAnd(t, ref p, cats);
            while (p < t.Count && t[p] == "|")
            {
                p++;
                bool rhs = EvalAnd(t, ref p, cats);
                lhs = lhs || rhs;
            }

            return lhs;
        }

        static bool EvalAnd(List<string> t, ref int p, IReadOnlySet<string> cats)
        {
            bool lhs = EvalAtom(t, ref p, cats);
            while (p < t.Count && t[p] == "&")
            {
                p++;
                bool rhs = EvalAtom(t, ref p, cats);
                lhs = lhs && rhs;
            }

            return lhs;
        }

        static bool EvalAtom(List<string> t, ref int p, IReadOnlySet<string> cats)
        {
            string tok = t[p++];
            if (tok == "(")
            {
                bool inner = EvalOr(t, ref p, cats);
                (p < t.Count && t[p] == ")").ShouldBeTrue("Missing closing ')' in filter expression");
                p++;
                return inner;
            }

            if (tok.StartsWith("Category!=", StringComparison.Ordinal))
            {
                return !cats.Contains(tok["Category!=".Length..]);
            }

            if (tok.StartsWith("Category=", StringComparison.Ordinal))
            {
                return cats.Contains(tok["Category=".Length..]);
            }

            throw new InvalidOperationException($"Unsupported category filter token: {tok}");
        }
    }

    private static (Dictionary<string, HashSet<string>> Classes, Dictionary<string, HashSet<string>> Methods) ScanIntegrationTraits()
    {
        var rawClasses = new Dictionary<string, (string Namespace, bool IsAbstract, string? BaseName, HashSet<string> Categories, Dictionary<string, HashSet<string>> Methods)>(StringComparer.Ordinal);
        var byShortName = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var relPath in RepositoryScan.EnumerateFiles("tests/Autheris.Tests.Integration", "*.cs"))
        {
            string source = RepositoryScan.ReadText(relPath);
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();
            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                string ns = cls.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "Autheris.Tests.Integration";
                string fullName = $"{ns}.{cls.Identifier.Text}";
                bool isAbstract = cls.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword));
                string? baseName = cls.BaseList?.Types
                    .Select(t => t.Type.ToString())
                    .FirstOrDefault(n => !n.StartsWith("IClassFixture", StringComparison.Ordinal) &&
                                         !n.StartsWith("IAsyncLifetime", StringComparison.Ordinal) &&
                                         !n.StartsWith("IDisposable", StringComparison.Ordinal));

                if (!rawClasses.TryGetValue(fullName, out var entry))
                {
                    entry = (ns, isAbstract, baseName, new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, HashSet<string>>(StringComparer.Ordinal));
                }
                else
                {
                    entry = (ns, entry.IsAbstract || isAbstract, entry.BaseName ?? baseName, entry.Categories, entry.Methods);
                }

                foreach (var cat in ExtractCategories(cls.AttributeLists))
                {
                    entry.Categories.Add(cat);
                }

                foreach (var method in cls.Members.OfType<MethodDeclarationSyntax>())
                {
                    if (!IsTestMethod(method.AttributeLists)) continue;
                    if (!entry.Methods.TryGetValue(method.Identifier.Text, out var mCats))
                    {
                        mCats = new HashSet<string>(StringComparer.Ordinal);
                        entry.Methods[method.Identifier.Text] = mCats;
                    }

                    foreach (var cat in ExtractCategories(method.AttributeLists))
                    {
                        mCats.Add(cat);
                    }
                }

                rawClasses[fullName] = entry;
                byShortName[cls.Identifier.Text] = fullName;
            }
        }

        var classes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var methods = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (fullName, info) in rawClasses)
        {
            if (info.IsAbstract) continue;

            var effectiveClassCats = new HashSet<string>(info.Categories, StringComparer.Ordinal);
            var allMethods = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (mName, mCats) in info.Methods)
            {
                allMethods[mName] = new HashSet<string>(mCats, StringComparer.Ordinal);
            }

            string? curBase = info.BaseName;
            while (curBase is not null)
            {
                string? resolvedBase = rawClasses.ContainsKey(curBase) ? curBase : (byShortName.TryGetValue(curBase, out var b) ? b : null);
                if (resolvedBase is null || !rawClasses.TryGetValue(resolvedBase, out var baseInfo)) break;

                foreach (var c in baseInfo.Categories) effectiveClassCats.Add(c);
                foreach (var (mName, mCats) in baseInfo.Methods)
                {
                    if (!allMethods.TryGetValue(mName, out var targetCats))
                    {
                        targetCats = new HashSet<string>(StringComparer.Ordinal);
                        allMethods[mName] = targetCats;
                    }

                    foreach (var c in mCats) targetCats.Add(c);
                }

                curBase = baseInfo.BaseName;
            }

            if (allMethods.Count == 0 && effectiveClassCats.Count == 0) continue;

            classes[fullName] = effectiveClassCats;
            foreach (var (mName, mCats) in allMethods)
            {
                var combined = new HashSet<string>(effectiveClassCats, StringComparer.Ordinal);
                foreach (var c in mCats) combined.Add(c);
                methods[$"{fullName}.{mName}"] = combined;
            }
        }

        return (classes, methods);
    }

    private static bool IsTestMethod(SyntaxList<AttributeListSyntax> lists)
    {
        foreach (var attr in lists.SelectMany(l => l.Attributes))
        {
            string name = attr.Name.ToString();
            if (name is "Fact" or "Theory" or "SparkFact" or "SparkTheory" or "RequiresContainerRuntimeFact" or "RequiresContainerRuntimeTheory" or "Property"
                or "FactAttribute" or "TheoryAttribute" or "SparkFactAttribute" or "SparkTheoryAttribute")
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> ExtractCategories(SyntaxList<AttributeListSyntax> lists)
    {
        foreach (var attr in lists.SelectMany(l => l.Attributes))
        {
            string name = attr.Name.ToString();
            if (name is not ("Trait" or "TraitAttribute" or "Xunit.Trait" or "Xunit.TraitAttribute")) continue;
            var args = attr.ArgumentList?.Arguments;
            if (args is null || args.Value.Count != 2) continue;
            if (args.Value[0].Expression is LiteralExpressionSyntax keyLit && keyLit.Token.ValueText == "Category" &&
                args.Value[1].Expression is LiteralExpressionSyntax valLit)
            {
                yield return valLit.Token.ValueText;
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
