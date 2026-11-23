using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

[Trait("Category", "Security")]
public sealed partial class JustificationPolicyArchitectureTests
{
    [GeneratedRegex(
        @"(?:MinJustificationLength\s*=\s*\d+)|(?:""minLength""\s*:\s*\d+)|(?:[Jj]ustification[^\r\n]*?\.Length\s*[<>]=?\s*\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex HardcodedJustificationLengthRegex();

    [Fact]
    public void No_other_justification_length_literals()
    {
        var violations = new List<string>();
        foreach (var file in RepositoryScan.ProductionFiles)
        {
            if (file.RelativePath.EndsWith("JustificationPolicy.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines = RepositoryScan.CodeLines(file.Text).ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (HardcodedJustificationLengthRegex().IsMatch(line))
                {
                    violations.Add($"{file.RelativePath}:{i + 1}: {line.Trim()}");
                }
            }
        }

        violations.ShouldBeEmpty();
    }
}
