namespace Autheris.Tests.Architecture;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class AuditDetailsArchitectureTests
{
    [GeneratedRegex(@"DetailsJson\s*=\s*(?:\$""|\$@""|@\$""|\$""""""|string\.Format\s*\()|(?:DetailsJson\s*=[^;\r\n]*\+\s*""|""\s*\+[^;\r\n]*;)", RegexOptions.CultureInvariant)]
    private static partial Regex InterpolatedOrConcatenatedDetailsJsonRegex();

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-SIMPL-50-04")]
    public void No_interpolated_audit_details_json()
    {
        var productionFiles = RepositoryScan.ProductionFiles
            .Concat(RepositoryScan.EnumerateFiles("modules", "*.cs")
                .Where(p => !p.Contains(".Tests/", StringComparison.OrdinalIgnoreCase))
                .Select(p => new RepositoryScan.SourceFile(p, RepositoryScan.ReadText(p))))
            .ToList();

        var violations = new List<string>();

        foreach (var file in productionFiles)
        {
            var lines = RepositoryScan.CodeLines(file.Text).ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.Contains("DetailsJson") && InterpolatedOrConcatenatedDetailsJsonRegex().IsMatch(line))
                {
                    violations.Add($"{file.RelativePath}:{i + 1}: {line.Trim()}");
                }
            }
        }

        violations.ShouldBeEmpty();
    }
}
