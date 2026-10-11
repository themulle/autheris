namespace Autheris.Tests.SqlCompilerGate;

using System.Text.RegularExpressions;
using Shouldly;

/// <summary>
/// Static assertions on the CI workflows (plan WP-X0w, SEC-X1-11, SEC-ADG-09 item 5, SEC-ADG-13): the live Databricks job (G9) holds
/// workspace secrets, so it must run from a protected GitHub environment and never from a pull request trigger.
/// The workflow files are read as text (no YAML dependency); the layout the assertions rely on is the standard two-space style.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class WorkflowPolicyTests
{
    private const string G9Job = "sql-compiler-gate-databricks-g9";

    [GeneratedRegex(@"^  ([A-Za-z0-9_-]+):\s*(#.*)?$")]
    private static partial Regex JobHeader();

    [GeneratedRegex(@"secrets\.DATABRICKS_", RegexOptions.CultureInvariant)]
    private static partial Regex DatabricksSecret();

    [Fact]
    public void Workflow_G9Job_UsesProtectedEnvironment_NoForkTrigger()
    {
        var workflows = LoadWorkflows();
        var withG9 = workflows.Where(w => Jobs(w.Lines).ContainsKey(G9Job)).ToList();
        withG9.Count.ShouldBe(1, $"exactly one workflow must define the job '{G9Job}'");

        var workflow = withG9[0];
        var job = Jobs(workflow.Lines)[G9Job];

        job.ShouldContain(l => Regex.IsMatch(l, @"^    environment:\s*\S+"), "the G9 job must be bound to a GitHub environment (required reviewers are configured on it)");
        job.ShouldContain(l => DatabricksSecret().IsMatch(l), "the G9 job is the consumer of the Databricks secrets");

        string triggers = string.Join('\n', Triggers(workflow.Lines));
        triggers.ShouldNotContain("pull_request", Case.Insensitive, "the file that holds the G9 job must not run on pull requests (no fork code with workspace secrets)");
        triggers.ShouldNotContain("workflow_run", Case.Insensitive);
        triggers.ShouldNotContain("labeled", Case.Insensitive, "a label alone never releases the secrets");

        foreach (var line in job)
        {
            Regex.IsMatch(line, @"^\s*if:.*(label|fork)", RegexOptions.IgnoreCase).ShouldBeFalse("the G9 job is not gated by a label: " + line);
        }
    }

    [Fact]
    public void Workflow_DatabricksSecrets_AreOnlyUsedByEnvironmentBoundJobs()
    {
        foreach (var workflow in LoadWorkflows())
        {
            foreach (var (name, lines) in Jobs(workflow.Lines))
            {
                if (!lines.Any(l => DatabricksSecret().IsMatch(l))) continue;
                lines.ShouldContain(l => Regex.IsMatch(l, @"^    environment:\s*\S+"), $"{workflow.Name}/{name} uses Databricks secrets without an environment");
            }
        }
    }

    [Fact]
    public void Workflow_SqlCompilerGateJob_RunsOnPullRequestMergeRef_WithoutRetries()
    {
        var ci = LoadWorkflows().Single(w => w.Name == "ci.yml");
        ci.Lines.ShouldContain(l => l.StartsWith("  pull_request:", StringComparison.Ordinal));
        Jobs(ci.Lines).ShouldContainKey("sql-compiler-gate");

        var job = Jobs(ci.Lines)["sql-compiler-gate"];
        job.ShouldContain(l => l.Contains("SQLGATE_PROFILE: pr", StringComparison.Ordinal));
        job.ShouldContain(l => l.Contains("-c Debug", StringComparison.Ordinal) && l.Contains("Category=SqlGate", StringComparison.Ordinal), "the security mutants run only in Debug");
        job.ShouldContain(l => l.Contains("-c Release", StringComparison.Ordinal) && l.Contains("Category=SqlGate", StringComparison.Ordinal), "the Release run proves the mutant hooks are absent");
        // The default checkout of a pull_request run is the merge ref; an explicit ref would test the PR head instead.
        job.ShouldNotContain(l => Regex.IsMatch(l, @"^\s*ref:\s"), "the gate must test the merge result, not the PR head");
        job.ShouldNotContain(l => l.Contains("retry", StringComparison.OrdinalIgnoreCase));
    }

    private static List<(string Name, string[] Lines)> LoadWorkflows()
    {
        string dir = Path.Combine(FindRoot(), ".github", "workflows");
        return Directory.EnumerateFiles(dir, "*.yml").Select(f => (Path.GetFileName(f), File.ReadAllLines(f))).ToList();
    }

    private static Dictionary<string, string[]> Jobs(string[] lines)
    {
        var result = new Dictionary<string, string[]>();
        int jobsAt = Array.FindIndex(lines, l => l.TrimEnd() == "jobs:");
        if (jobsAt < 0) return result;

        string? current = null;
        var buffer = new List<string>();
        for (int i = jobsAt + 1; i < lines.Length; i++)
        {
            var match = JobHeader().Match(lines[i]);
            if (match.Success)
            {
                if (current is not null) result[current] = buffer.ToArray();
                current = match.Groups[1].Value;
                buffer = new List<string>();
            }
            else if (current is not null)
            {
                buffer.Add(lines[i]);
            }
        }

        if (current is not null) result[current] = buffer.ToArray();
        return result;
    }

    /// <summary>The lines of the top-level <c>on:</c> block.</summary>
    private static IEnumerable<string> Triggers(string[] lines)
    {
        int start = Array.FindIndex(lines, l => l.StartsWith("on:", StringComparison.Ordinal));
        if (start < 0) yield break;
        yield return lines[start];
        for (int i = start + 1; i < lines.Length && (lines[i].Length == 0 || lines[i][0] == ' ' || lines[i][0] == '#'); i++)
        {
            yield return lines[i];
        }
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) return dir.FullName;
        }

        throw new InvalidOperationException("Autheris.sln was not found above " + AppContext.BaseDirectory);
    }
}
