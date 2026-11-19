using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// The Trino client interop workflow is a nightly job that downloads third-party artifacts: it must stay free of secrets and untrusted
/// triggers, pin every action by commit SHA, verify the JDBC jar by SHA-256 and install Python packages only with hashes. The scanners are
/// pure and each has a negative self-check on an in-memory sample.
/// </summary>
public sealed partial class WorkflowLintTests
{
    private const string WorkflowPath = ".github/workflows/trino-interop-nightly.yml";
    private const string RequirementsPath = "tests/Autheris.Tests.Integration/Interop/Trino/requirements-trino-interop.txt";

    [GeneratedRegex(@"\bsecrets\b", RegexOptions.IgnoreCase)]
    private static partial Regex SecretsPattern();

    [GeneratedRegex(@"\buses:\s*(?<ref>[^\s#]+)")]
    private static partial Regex UsesPattern();

    [GeneratedRegex(@"^[^@]+@[0-9a-f]{40}$")]
    private static partial Regex ShaPinnedPattern();

    [GeneratedRegex(@"TRINO_JDBC_SHA256:\s*'(?<sha>[0-9a-f]{64})'")]
    private static partial Regex JdbcShaPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+==[A-Za-z0-9_.!+-]+(\s+--hash=sha256:[0-9a-f]{64})+\s*$")]
    private static partial Regex HashedRequirementPattern();

    internal static IReadOnlyList<string> WorkflowViolations(string workflow)
    {
        var violations = new List<string>();
        var code = string.Join('\n', workflow.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

        var triggers = ParityIsolationArchitectureTests.TriggerKeys(workflow);
        if (!triggers.OrderBy(t => t, StringComparer.Ordinal).SequenceEqual(["schedule", "workflow_dispatch"]))
        {
            violations.Add($"triggers must be exactly schedule and workflow_dispatch, found [{string.Join(", ", triggers)}]");
        }

        if (code.Contains("pull_request", StringComparison.Ordinal))
        {
            violations.Add("pull_request and pull_request_target are forbidden");
        }

        if (SecretsPattern().IsMatch(code))
        {
            violations.Add("the workflow must not reference secrets");
        }

        if (!Regex.IsMatch(code, @"(?m)^permissions:\s*\{\}\s*$"))
        {
            violations.Add("top-level permissions must be {}");
        }

        foreach (Match inline in Regex.Matches(code, @"(?m)^[ \t]+permissions:[ \t]*(?<value>\S.*)$"))
        {
            violations.Add($"job-level permissions must use the block form, found inline value: {inline.Groups["value"].Value.Trim()}");
        }

        foreach (Match flow in Regex.Matches(code, @"\{[^}\n]*\bwrite\b[^}\n]*\}"))
        {
            violations.Add($"flow map with a write permission: {flow.Value}");
        }

        if (Regex.IsMatch(code, @"(?m)^[ \t]+inputs:[ \t]*$") || code.Contains("github.event.inputs", StringComparison.Ordinal))
        {
            violations.Add("workflow_dispatch must not take inputs");
        }

        var steps = Regex.Split(code, @"(?m)^[ \t]+- name:").Skip(1).ToList();
        foreach (var step in steps)
        {
            var lines = step.Split('\n');
            var runIndex = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^[ \t]+run:"));
            if (runIndex < 0)
            {
                continue;
            }

            var indent = lines[runIndex].Length - lines[runIndex].TrimStart().Length;
            var runLines = new List<string> { lines[runIndex] };
            runLines.AddRange(lines.Skip(runIndex + 1).TakeWhile(l => l.Trim().Length == 0 || l.Length - l.TrimStart().Length > indent));
            var runText = string.Join('\n', runLines);
            if (runText.Contains("${{", StringComparison.Ordinal))
            {
                violations.Add("run blocks must not interpolate expressions: " + lines[0].Trim());
            }

            var export = runText.IndexOf("AUTHERIS_TRINO_JDBC_JAR=", StringComparison.Ordinal);
            if (export >= 0)
            {
                var check = runText.IndexOf("sha256sum --check", StringComparison.Ordinal);
                if (check < 0 || check > export)
                {
                    violations.Add("sha256sum --check must run before the jar is exported");
                }

                if (!runText.Contains("set -euo pipefail", StringComparison.Ordinal))
                {
                    violations.Add("the jar download block must use set -euo pipefail");
                }
            }
        }

        foreach (Match write in Regex.Matches(code, @"(?m)^\s+(?<scope>[a-z-]+):\s*write\s*$"))
        {
            if (write.Groups["scope"].Value != "issues")
            {
                violations.Add($"unexpected write permission: {write.Groups["scope"].Value}");
            }
        }

        var uses = UsesPattern().Matches(code).Select(m => m.Groups["ref"].Value).ToList();
        if (uses.Count == 0)
        {
            violations.Add("no actions found: the scanner fails closed");
        }

        violations.AddRange(uses.Where(u => !ShaPinnedPattern().IsMatch(u)).Select(u => $"action not pinned by a 40-hex commit SHA: {u}"));

        if (!JdbcShaPattern().IsMatch(code) || !code.Contains("sha256sum --check", StringComparison.Ordinal))
        {
            violations.Add("the JDBC jar must be pinned by a 64-hex SHA-256 and verified with sha256sum --check");
        }

        var pipLines = code.Split('\n').Where(l => Regex.IsMatch(l, @"\bpip\b.*\binstall\b")).ToList();
        if (pipLines.Count == 0)
        {
            violations.Add("no pip install found: the scanner fails closed");
        }

        violations.AddRange(pipLines.Where(l => !l.Contains("--require-hashes", StringComparison.Ordinal)).Select(l => $"pip install without --require-hashes: {l.Trim()}"));
        return violations;
    }

    internal static IReadOnlyList<string> RequirementViolations(string requirements)
    {
        var lines = requirements.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        if (lines.Count == 0)
        {
            return ["no requirements found: the scanner fails closed"];
        }

        return lines.Where(l => !HashedRequirementPattern().IsMatch(l)).Select(l => $"requirement without exact pin and sha256 hash: {l}").ToList();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Trino_interop_workflow_has_no_secrets_and_pinned_artifacts()
    {
        WorkflowViolations(RepositoryScan.ReadText(WorkflowPath)).ShouldBeEmpty();
        RequirementViolations(RepositoryScan.ReadText(RequirementsPath)).ShouldBeEmpty();

        // Negative self-checks: each defect is caught.
        var good = RepositoryScan.ReadText(WorkflowPath);
        WorkflowViolations(good.Replace("  workflow_dispatch:", "  pull_request_target:", StringComparison.Ordinal)).ShouldNotBeEmpty();
        WorkflowViolations(good + "\n      FOO: ${{ secrets.TOKEN }}\n").ShouldNotBeEmpty();
        WorkflowViolations(good.Replace("permissions: {}", "permissions: write-all", StringComparison.Ordinal)).ShouldNotBeEmpty();
        WorkflowViolations(good.Replace("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1", "actions/checkout@v4", StringComparison.Ordinal)).ShouldNotBeEmpty();
        WorkflowViolations(good.Replace("--require-hashes", string.Empty, StringComparison.Ordinal)).ShouldNotBeEmpty();
        WorkflowViolations(good.Replace("sha256sum --check", "echo", StringComparison.Ordinal)).ShouldNotBeEmpty();
        WorkflowViolations(good.Replace("contents: read", "contents: write", StringComparison.Ordinal)).ShouldNotBeEmpty();
        WorkflowViolations(good.Replace("failure() && github.event_name == 'schedule'", "github.event_name == 'pull_request'", StringComparison.Ordinal)).ShouldNotBeEmpty();
        WorkflowViolations(good.Replace("    permissions:\n      contents: read\n    env:", "    permissions: write-all\n    env:", StringComparison.Ordinal)).ShouldNotBeEmpty();
        WorkflowViolations(good.Replace("    permissions:\n      contents: read\n    env:", "    permissions: { contents: write }\n    env:", StringComparison.Ordinal)).ShouldNotBeEmpty();
        WorkflowViolations(good.Replace("  workflow_dispatch:\n", "  workflow_dispatch:\n    inputs:\n      x:\n        type: string\n", StringComparison.Ordinal)).ShouldNotBeEmpty();
        WorkflowViolations(good.Replace("run: dotnet restore tests/Autheris.Tests.Integration --locked-mode", "run: echo ${{ github.head_ref }}", StringComparison.Ordinal)).ShouldNotBeEmpty();
        WorkflowViolations(good.Replace("          set -euo pipefail\n          mkdir -p", "          mkdir -p", StringComparison.Ordinal)).ShouldNotBeEmpty();
        WorkflowViolations(SwapJarCheckAndExport(good)).ShouldNotBeEmpty();
        WorkflowViolations("on:\n  schedule:\n    - cron: '0 3 * * *'\n  workflow_dispatch:\npermissions: {}\n").ShouldContain("no actions found: the scanner fails closed");
        WorkflowViolations(good.Replace("pip install", "pip3 fetch", StringComparison.Ordinal)).ShouldContain("no pip install found: the scanner fails closed");
        RequirementViolations("trino==0.340.0\n").ShouldNotBeEmpty();
        RequirementViolations("trino>=0.340.0 --hash=sha256:" + new string('a', 64)).ShouldNotBeEmpty();
        RequirementViolations(string.Empty).ShouldNotBeEmpty();
    }

    private static string SwapJarCheckAndExport(string workflow)
    {
        const string check = "          echo \"${TRINO_JDBC_SHA256}  $RUNNER_TEMP/trino-interop/trino-jdbc.jar\" | sha256sum --check --strict -\n";
        const string export = "          echo \"AUTHERIS_TRINO_JDBC_JAR=$RUNNER_TEMP/trino-interop/trino-jdbc.jar\" >> \"$GITHUB_ENV\"\n";
        workflow.ShouldContain(check);
        workflow.ShouldContain(export);
        return workflow.Replace(check + export, export + check, StringComparison.Ordinal);
    }
}
