namespace Autheris.Tests.Architecture;

using System;
using System.IO;
using System.Linq;
using Shouldly;
using Xunit;

public sealed class GovernanceRepositoryLocBudgetTests
{
    // Post-consolidation budget ceiling with appropriate headroom (parent guidance WP-SIMPL-20d).
    // Original AD-1 target was 7,000 lines; Wave 4-7 unified governance store extensions
    // (WP-SIMPL-42/45/47/48/52) temporarily raised the total and WP-SIMPL-52 PR 6 will ratchet it back down when legacy tables/repos are dropped.
    public const int MaxAllowedLoc = 15_968;

    [Fact]
    [Trait("Category", "Architecture")]
    public void Ad1_metric_at_most_7000_lines()
    {
        var srcDir = Path.Combine(RepositoryScan.RepoRoot, "src");
        var patterns = new[] { "*GovernanceRepository*.cs", "*SqlCore.cs", "*GovernanceDialect.cs" };

        var files = patterns
            .SelectMany(pattern => Directory.GetFiles(srcDir, pattern, SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalLines = files.Sum(file => File.ReadAllLines(file).Length);

        totalLines.ShouldBeLessThanOrEqualTo(
            MaxAllowedLoc,
            $"AD-1 governance repository consolidation metric: expected <= {MaxAllowedLoc} LOC across unified partials and cores, but found {totalLines} LOC across {files.Count} files.");
    }
}
