using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

public sealed class HelmValuesTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void Image_tag_is_pinned_and_not_latest()
    {
        var valuesPath = Path.Combine(RepositoryScan.RepoRoot, "deploy", "helm", "autheris", "values.yaml");
        var helpersPath = Path.Combine(RepositoryScan.RepoRoot, "deploy", "helm", "autheris", "templates", "_helpers.tpl");

        File.Exists(valuesPath).ShouldBeTrue();
        File.Exists(helpersPath).ShouldBeTrue();

        var valuesContent = File.ReadAllText(valuesPath);
        var helpersContent = File.ReadAllText(helpersPath);

        // Extract image.tag from values.yaml
        var tagMatch = Regex.Match(valuesContent, @"tag:\s*""?([^""\r\n]*)""?");
        tagMatch.Success.ShouldBeTrue("image.tag must be present in values.yaml");

        var tagValue = tagMatch.Groups[1].Value.Trim();

        // Never allow "latest" as default image tag
        tagValue.ToLowerInvariant().ShouldNotBe("latest");

        // Must be empty (to fall back to AppVersion) or a sha256 digest
        var isTagValid = string.IsNullOrEmpty(tagValue) || tagValue.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase);
        isTagValid.ShouldBeTrue($"image.tag must be empty or a digest, but was '{tagValue}'");

        // Helper autheris.image must fall back to .Chart.AppVersion
        helpersContent.ShouldContain("default .Chart.AppVersion .Values.image.tag");
    }
}
