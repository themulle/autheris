using System.Text.RegularExpressions;
using Autheris.Domain.Security;
using Shouldly;

namespace Autheris.Tests.Architecture;

[Trait("Category", "Security")]
public sealed partial class ProtectionClassParseArchitectureTests
{
    private const string ProtectionClassFileRelativePath = "src/Autheris.Domain/Security/ProtectionClass.cs";

    [GeneratedRegex(@"\bEnum\s*\.\s*(?:Try)?Parse\s*<\s*ProtectionClass\s*>|\bEnum\s*\.\s*(?:Try)?Parse\s*\(\s*typeof\s*\(\s*ProtectionClass\s*\)|\bdefault\s*\(\s*ProtectionClass\s*\)|\bnew\s+ProtectionClass\s*\(\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenProtectionClassDefaultOrParseRegex();

    [Fact]
    public void No_default_public_on_parse_failure()
    {
        var files = RepositoryScan.ProductionFiles;
        files.Count.ShouldBeGreaterThan(500);

        var violations = new List<string>();
        foreach (var file in files)
        {
            if (string.Equals(file.RelativePath, ProtectionClassFileRelativePath, StringComparison.Ordinal))
            {
                continue;
            }

            if (ForbiddenProtectionClassDefaultOrParseRegex().IsMatch(file.Text))
            {
                violations.Add(file.RelativePath);
            }
        }

        violations.ShouldBeEmpty(
            "ProtectionClass must only be parsed via ProtectionClassExtensions and never default to Public on failure.");

        foreach (var invalid in new[] { null, string.Empty, "unknown", "0" })
        {
            ProtectionClassExtensions.ParseOrConfidential(invalid).ShouldBe(ProtectionClass.Confidential);
            ProtectionClassExtensions.TryParse(invalid, out var parsed).ShouldBeFalse();
            parsed.ShouldBe(ProtectionClass.Confidential);
        }
    }
}
