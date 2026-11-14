namespace Autheris.Tests.Architecture;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class ModuleBoundaryTests
{
    [GeneratedRegex(@"new\s+(?:Autheris\.Application\.Policy\.)?AccessDecisionPoint\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex NewAccessDecisionPointRegex();

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-SIMPL-50-02")]
    public void No_null_policy_service_in_production_construction()
    {
        var repoRoot = RepositoryScan.RepoRoot;
        var productionFiles = RepositoryScan.ProductionFiles
            .Concat(RepositoryScan.EnumerateFiles("modules", "*.cs")
                .Where(p => !p.Contains(".Tests/", StringComparison.OrdinalIgnoreCase))
                .Select(p => new RepositoryScan.SourceFile(p, RepositoryScan.ReadText(p))))
            .ToList();

        var violations = new List<string>();

        foreach (var file in productionFiles)
        {
            var text = file.Text;
            if (NewAccessDecisionPointRegex().IsMatch(text))
            {
                violations.Add($"{file.RelativePath}: production code must not hand-construct AccessDecisionPoint");
            }
        }

        violations.ShouldBeEmpty();
    }
}
