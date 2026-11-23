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
    // (WP-SIMPL-41/42/45/47/48/52) temporarily raised the total and WP-SIMPL-52 PR 6 will ratchet it back down when legacy tables/repos are dropped.
    // #1009 W3 permission request engine persistence on top of the #1043 consent batch reads (+49 LOC over 15,818).
    // #1009 W8-02 scoped audit read with owner scoping and bound cursors in AuditSqlCore (+18 LOC).
    // #1009 W7-03 deprovisioning revokes all grant kinds; kind-complete read + tenant-scoped by-id revoke (+88 LOC).
    public const int MaxAllowedLoc = 15_973;

    [Fact]
    [Trait("Category", "Architecture")]
    public void Ad1_metric_is_a_shrink_only_ratchet()
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

    [Fact]
    [Trait("Category", "Architecture")]
    public void Only_AuditSqlCore_keeps_the_SqlCore_suffix()
    {
        var allowed = new[] { "AuditSqlCore.cs" };
        var actual = Directory.GetFiles(Path.Combine(RepositoryScan.RepoRoot, "src"), "*SqlCore.cs", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        actual.Where(n => !allowed.Contains(n)).ShouldBeEmpty("No new *SqlCore.cs files: SQL lives in GovernanceRepository partials.");
    }
}
