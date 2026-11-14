namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class RequiredFilterArchitectureTests
{
    private static readonly Regex RuleCallWithReferencedColumns = new(
        @"RequiredFilterColumnRule\s*\.\s*(?:IsColumnSatisfied|IsSatisfied|Enforce)\s*\([^;]*?\bReferencedColumns\b",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    [Fact]
    [Trait("Category", "Security")]
    public void RequiredFilterColumnRule_NeverCalledWithReferencedColumns()
    {
        var root = FindRepoRoot();
        var srcDir = Path.Combine(root, "src");
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var content = File.ReadAllText(file);
            foreach (Match match in RuleCallWithReferencedColumns.Matches(content))
            {
                var line = 1 + content[..match.Index].Count(c => c == '\n');
                violations.Add($"{Path.GetRelativePath(root, file)}:{line}");
            }
        }

        violations.ShouldBeEmpty(
            "RequiredFilterColumnRule must receive ConstrainedColumns, never ReferencedColumns: " + string.Join(", ", violations));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void RequiredFilterColumnRule_Scanner_DetectsPositiveControl()
    {
        const string sample = """
            RequiredFilterColumnRule.IsColumnSatisfied(reqCol, metadata, decision, filter?.ReferencedColumns, true);
            """;
        RuleCallWithReferencedColumns.IsMatch(sample).ShouldBeTrue();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Autheris.sln not found.");
        return dir.FullName;
    }
}
