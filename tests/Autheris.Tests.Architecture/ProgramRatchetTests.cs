using System.Text.RegularExpressions;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Autheris.Tests.Architecture;

public sealed partial class ProgramRatchetTests(ITestOutputHelper output)
{
    private static readonly string[] SkippedAppFolders = ["bin", "obj", "Properties"];

    [GeneratedRegex(@"\.Map(Get|Post|Put|Delete|Patch|Methods|Group|Hub|GraphQL)\s*\(")]
    private static partial Regex EndpointRegex();

    [GeneratedRegex(@"class\s+\w+Options\b")]
    private static partial Regex OptionsClassRegex();

    [GeneratedRegex(@"public\s+bool\s+\w*Enabled\s*\{")]
    private static partial Regex EnabledFlagRegex();

    [GeneratedRegex(@"\b(REV-\d+|SEC-[A-Z0-9-]+|AST-X\d+|ARCH-MNT-\d+|PLAN-[A-Z]+-\d+|WP-[A-Z0-9.-]+|REQ-[A-Z]+-\d+)\b")]
    private static partial Regex PlanIdRegex();

    internal static bool IsCommentLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal);
    }

    internal static int CountEndpointsInLine(string line) =>
        IsCommentLine(line) ? 0 : EndpointRegex().Count(line);

    internal static int CountOptionsInLine(string line) =>
        IsCommentLine(line) ? 0 : OptionsClassRegex().Count(line);

    internal static int CountEnabledInLine(string line) =>
        IsCommentLine(line) ? 0 : EnabledFlagRegex().Count(line);

    internal static int CountPlanIdsInLine(string line) =>
        IsCommentLine(line) ? PlanIdRegex().Count(line) : 0;

    [Fact]
    public void Endpoint_count_does_not_grow()
    {
        var baseline = RepositoryScan.ParseCountBaseline(RepositoryScan.ReadBaseline("endpoint-count.txt"));
        var expected = baseline["total"];

        var actual = RepositoryScan.ProductionFiles
            .Sum(f => Regex.Split(f.Text, "\r\n|\r|\n").Sum(CountEndpointsInLine));

        output.WriteLine($"Endpoints: actual={actual}, baseline={expected}");

        if (actual > expected)
        {
            actual.ShouldBeLessThanOrEqualTo(
                expected,
                $"Endpoint count grew: actual {actual} > baseline {expected}. " +
                "ratchet: lower the baseline when you delete; raising it is allowed only in the same PR with label freeze-exception (security fix or owner-mandated WP-SIMPL-38..47, SEC-SIMPL-70), checked by the CI step pr-ratchet-baselines.sh.");
        }
        else if (actual < expected)
        {
            actual.ShouldBeGreaterThanOrEqualTo(
                expected,
                $"Endpoint baseline is stale, lower it to {actual}.");
        }
    }

    [Fact]
    public void Options_class_count_does_not_grow()
    {
        var baseline = RepositoryScan.ParseCountBaseline(RepositoryScan.ReadBaseline("options-class-count.txt"));
        var expected = baseline["total"];

        var actual = RepositoryScan.ProductionFiles
            .Sum(f => Regex.Split(f.Text, "\r\n|\r|\n").Sum(CountOptionsInLine));

        output.WriteLine($"Options classes: actual={actual}, baseline={expected}");

        if (actual > expected)
        {
            actual.ShouldBeLessThanOrEqualTo(
                expected,
                $"Options class count grew: actual {actual} > baseline {expected}. " +
                "ratchet: lower the baseline when you delete; raising it is allowed only in the same PR with label freeze-exception (security fix or owner-mandated WP-SIMPL-38..47, SEC-SIMPL-70), checked by the CI step pr-ratchet-baselines.sh.");
        }
        else if (actual < expected)
        {
            actual.ShouldBeGreaterThanOrEqualTo(
                expected,
                $"Options class baseline is stale, lower it to {actual}.");
        }
    }

    [Fact]
    public void Enabled_flag_count_does_not_grow()
    {
        var baseline = RepositoryScan.ParseCountBaseline(RepositoryScan.ReadBaseline("enabled-flag-count.txt"));
        var expected = baseline["total"];

        var actual = RepositoryScan.ProductionFiles
            .Sum(f => Regex.Split(f.Text, "\r\n|\r|\n").Sum(CountEnabledInLine));

        output.WriteLine($"Enabled flags: actual={actual}, baseline={expected}");

        if (actual > expected)
        {
            actual.ShouldBeLessThanOrEqualTo(
                expected,
                $"Enabled flag count grew: actual {actual} > baseline {expected}. " +
                "ratchet: lower the baseline when you delete; raising it is allowed only in the same PR with label freeze-exception (security fix or owner-mandated WP-SIMPL-38..47, SEC-SIMPL-70), checked by the CI step pr-ratchet-baselines.sh.");
        }
        else if (actual < expected)
        {
            actual.ShouldBeGreaterThanOrEqualTo(
                expected,
                $"Enabled flag baseline is stale, lower it to {actual}.");
        }
    }

    [Fact]
    public void Application_top_level_folders_are_allow_listed()
    {
        var baselineFolders = RepositoryScan.ReadBaseline("application-folders.txt")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var appDir = Path.Combine(RepositoryScan.RepoRoot, "src", "Autheris.Application");
        var actualFolders = Directory.GetDirectories(appDir)
            .Select(Path.GetFileName)
            .Where(d => !string.IsNullOrEmpty(d) && !SkippedAppFolders.Contains(d, StringComparer.OrdinalIgnoreCase))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        output.WriteLine($"Application folders: actual={actualFolders.Count}, baseline={baselineFolders.Count}");

        var disallowed = actualFolders.Except(baselineFolders, StringComparer.Ordinal).ToList();
        disallowed.ShouldBeEmpty(
            $"Application folder(s) not in allow-list: {string.Join(", ", disallowed)}. " +
            "ratchet: lower the baseline when you delete; raising it is allowed only in the same PR with label freeze-exception (security fix or owner-mandated WP-SIMPL-38..47, SEC-SIMPL-70), checked by the CI step pr-ratchet-baselines.sh.");

        var stale = baselineFolders.Except(actualFolders, StringComparer.Ordinal).ToList();
        stale.ShouldBeEmpty($"Application folders baseline is stale, lower it to actual folders by removing: {string.Join(", ", stale)}.");
    }

    [Fact]
    public void Application_top_level_folder_count_is_at_most_32()
    {
        var appDir = Path.Combine(RepositoryScan.RepoRoot, "src", "Autheris.Application");
        var actualFolders = Directory.GetDirectories(appDir)
            .Select(Path.GetFileName)
            .Where(d => !string.IsNullOrEmpty(d) && !SkippedAppFolders.Contains(d, StringComparer.OrdinalIgnoreCase))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        output.WriteLine($"Application top-level folders: {actualFolders.Count} (max 32)");
        actualFolders.Count.ShouldBeLessThanOrEqualTo(
            32,
            $"WP-SIMPL-24: src/Autheris.Application top-level folder count ({actualFolders.Count}) must be <= 32. Folders: {string.Join(", ", actualFolders)}");
    }

    [Fact]
    public void Plan_id_comments_do_not_grow()
    {
        var baseline = RepositoryScan.ParseCountBaseline(RepositoryScan.ReadBaseline("plan-id-comment-counts.txt"));

        var actualByProject = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in RepositoryScan.ProductionFiles)
        {
            var segments = file.RelativePath.Split('/');
            var project = segments.Length > 1 ? segments[1] : "src";
            var count = Regex.Split(file.Text, "\r\n|\r|\n").Sum(CountPlanIdsInLine);
            if (count > 0)
            {
                actualByProject[project] = actualByProject.GetValueOrDefault(project) + count;
            }
        }

        var allProjects = actualByProject.Keys.Union(baseline.Keys, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal);
        var problems = new List<string>();
        var stale = new List<string>();

        foreach (var project in allProjects)
        {
            var actual = actualByProject.GetValueOrDefault(project, 0);
            var expected = baseline.GetValueOrDefault(project, 0);

            output.WriteLine($"Plan-id comments [{project}]: actual={actual}, baseline={expected}");

            if (actual > expected)
            {
                problems.Add($"Project '{project}': plan-id comments grew ({actual} > {expected})");
            }
            else if (actual < expected)
            {
                stale.Add($"Project '{project}': baseline is stale ({actual} < {expected})");
            }
        }

        problems.ShouldBeEmpty(
            $"Plan-id comments grew in project(s): {string.Join("; ", problems)}. " +
            "ratchet: lower the baseline when you delete; raising it is allowed only in the same PR with label freeze-exception (security fix or owner-mandated WP-SIMPL-38..47, SEC-SIMPL-70), checked by the CI step pr-ratchet-baselines.sh.");

        stale.ShouldBeEmpty($"Plan-id comment baseline is stale: {string.Join("; ", stale)}; lower the baseline.");
    }

    [Theory]
    [InlineData("app.MapGet(\"/x\", ...)", "endpoint", 1)]
    [InlineData("// app.MapGet(", "endpoint", 0)]
    [InlineData("IOptions<FooOptions>", "options", 0)]
    [InlineData("public class FooOptions", "options", 1)]
    [InlineData("public bool FooEnabled { get; set; }", "enabled", 1)]
    [InlineData("// SEC-SIMPL-70 note", "plan-id", 1)]
    public void Counting_rules_match_known_samples(string sample, string kind, int expected)
    {
        var actual = kind switch
        {
            "endpoint" => CountEndpointsInLine(sample),
            "options" => CountOptionsInLine(sample),
            "enabled" => CountEnabledInLine(sample),
            "plan-id" => CountPlanIdsInLine(sample),
            _ => throw new ArgumentException($"Unknown kind: {kind}")
        };

        actual.ShouldBe(expected);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Pr_template_declares_freeze_and_size_labels()
    {
        var template = RepositoryScan.ReadText(".github/PULL_REQUEST_TEMPLATE.md");
        template.ShouldContain("freeze-exception");
        template.ShouldContain("size-exception");
        template.ShouldContain("SEC-SIMPL-70");
        template.ShouldContain("WP-SIMPL-38..47");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Ci_checks_ratchet_baseline_raises_against_freeze_exception_label()
    {
        var ci = RepositoryScan.ReadText(".github/workflows/ci.yml");
        ci.ShouldContain("pr-ratchet-baselines.sh");
        ci.ShouldContain("pull_request");
        ci.ShouldContain("PR_LABELS");

        var script = RepositoryScan.ReadText(".github/scripts/pr-ratchet-baselines.sh");
        script.ShouldContain("freeze-exception");
        script.ShouldContain("endpoint-count.txt");
        script.ShouldContain("options-class-count.txt");
        script.ShouldContain("enabled-flag-count.txt");
        script.ShouldContain("application-folders.txt");
        script.ShouldContain("plan-id-comment-counts.txt");
    }
}
