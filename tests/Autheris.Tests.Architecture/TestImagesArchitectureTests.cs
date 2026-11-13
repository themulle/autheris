using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// WP-OI-CI-4: every Testcontainers image comes from one digest-pinned list (<c>TestImages</c>) that the CI workflow pre-pulls
/// from <c>tests/test-images.txt</c>.
/// </summary>
public sealed partial class TestImagesArchitectureTests
{
    private const string TestImagesPath = "tests/Autheris.Tests.Common/TestImages.cs";
    private const string ImageListPath = "tests/test-images.txt";

    [GeneratedRegex(@"(?<!String)Builder\(\s*\$?@?""|\bWithImage\(\s*\$?@?""")]
    private static partial Regex ImageLiteralRegex();

    [GeneratedRegex("""public const string (\w+) = "([^"]+)";""")]
    private static partial Regex ConstantRegex();

    private static List<(string Name, string Value)> Constants() =>
        ConstantRegex().Matches(RepositoryScan.ReadText(TestImagesPath)).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();

    [Fact]
    public void Every_testcontainers_image_comes_from_TestImages()
    {
        var offenders = RepositoryScan.EnumerateFiles("tests", "*.cs")
            .Where(p => !p.EndsWith("/TestImages.cs", StringComparison.Ordinal) && !p.EndsWith("/TestImagesArchitectureTests.cs", StringComparison.Ordinal))
            .Where(p => RepositoryScan.CodeLines(RepositoryScan.ReadText(p)).Any(l => ImageLiteralRegex().IsMatch(l)))
            .ToList();

        offenders.ShouldBeEmpty("Use TestImages constants instead of image string literals: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_image_is_digest_pinned()
    {
        var constants = Constants();
        constants.Count.ShouldBeGreaterThanOrEqualTo(5);
        foreach (var (name, value) in constants)
        {
            Regex.IsMatch(value, "@sha256:[0-9a-f]{64}$").ShouldBeTrue($"TestImages.{name} must be pinned by @sha256 digest: {value}");
        }
    }

    [Fact]
    public void Workflow_prepull_list_matches_TestImages()
    {
        var listed = RepositoryScan.ReadText(ImageListPath)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith('#'))
            .Order(StringComparer.Ordinal)
            .ToList();
        var expected = Constants().Select(c => c.Value).Order(StringComparer.Ordinal).ToList();

        listed.ShouldBe(expected);
        RepositoryScan.ReadText(".github/workflows/ci.yml").ShouldContain(ImageListPath);
    }
}
