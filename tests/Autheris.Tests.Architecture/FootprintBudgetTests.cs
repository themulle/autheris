namespace Autheris.Tests.Architecture;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Shouldly;
using Xunit;

public sealed class FootprintBudgetTests
{
    [Fact]
    public void Budgets_file_has_all_required_metrics_with_positive_budgets()
    {
        var budgetsPath = Path.Combine(RepositoryScan.RepoRoot, "benchmarks", "budgets.json");
        File.Exists(budgetsPath).ShouldBeTrue("benchmarks/budgets.json must exist.");

        using var doc = JsonDocument.Parse(File.ReadAllText(budgetsPath));
        var root = doc.RootElement;

        root.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        root.GetProperty("tolerancePercent").GetDouble().ShouldBe(10.0);

        var metrics = root.GetProperty("metrics");
        var keys = metrics.EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToList();
        keys.ShouldBe(
        [
            "ciPrWallClockMedianMinutes",
            "coldStartP50Seconds",
            "coreImageUncompressedMB",
            "warmP99Milliseconds"
        ]);

        foreach (var prop in metrics.EnumerateObject())
        {
            var budget = prop.Value.GetProperty("budget").GetDouble();
            budget.ShouldBeGreaterThan(0.0, $"Metric '{prop.Name}' must have a positive budget.");
            prop.Value.GetProperty("unit").GetString().ShouldNotBeNullOrWhiteSpace();
            prop.Value.GetProperty("source").GetString().ShouldNotBeNullOrWhiteSpace();
        }

        metrics.GetProperty("coreImageUncompressedMB").GetProperty("budget").GetDouble().ShouldBe(325.0);
        metrics.GetProperty("coldStartP50Seconds").GetProperty("budget").GetDouble().ShouldBe(3.0);
        metrics.GetProperty("warmP99Milliseconds").GetProperty("budget").GetDouble().ShouldBe(3.47);

        var ciMetric = metrics.GetProperty("ciPrWallClockMedianMinutes");
        ciMetric.GetProperty("budget").GetDouble().ShouldBe(30.0);
        ciMetric.GetProperty("tolerancePercent").GetDouble().ShouldBe(0.0);
        ciMetric.GetProperty("comparison").GetString().ShouldBe("lessThan");
    }

    [Fact]
    public void Footprint_workflow_runs_nightly_and_on_release()
    {
        var workflowPath = Path.Combine(RepositoryScan.RepoRoot, ".github", "workflows", "footprint-budget.yml");
        File.Exists(workflowPath).ShouldBeTrue(".github/workflows/footprint-budget.yml must exist.");
        var workflowText = File.ReadAllText(workflowPath);

        workflowText.ShouldContain("schedule:");
        workflowText.ShouldContain("cron:");
        workflowText.ShouldContain("workflow_dispatch:");
        workflowText.ShouldContain("workflow_call:");
        workflowText.ShouldContain("benchmarks/scripts/footprint.sh");
        workflowText.ShouldContain("benchmarks/scripts/ci-duration.sh");
        workflowText.ShouldContain("benchmarks/scripts/check-budgets.sh");

        var releasePath = Path.Combine(RepositoryScan.RepoRoot, ".github", "workflows", "release.yml");
        File.Exists(releasePath).ShouldBeTrue();
        var releaseText = File.ReadAllText(releasePath);
        releaseText.ShouldContain("footprint-budget.yml");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Workflow_permissions_are_read_only()
    {
        var workflowPath = Path.Combine(RepositoryScan.RepoRoot, ".github", "workflows", "footprint-budget.yml");
        File.Exists(workflowPath).ShouldBeTrue(".github/workflows/footprint-budget.yml must exist.");
        var workflowText = File.ReadAllText(workflowPath);

        workflowText.ShouldContain("contents: read");
        workflowText.ShouldNotContain("write", Case.Insensitive);
    }
}
