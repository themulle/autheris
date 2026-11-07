using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autheris.Api.Configuration;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

public sealed class DocsInventoryTests
{
    private static readonly string[] RequiredReadmeHeadings =
    [
        "## Core capabilities",
        "## Transports",
        "## Modules",
        "## Quick start",
        "## Authentication",
        "## Project status",
        "## Benchmarks",
        "## Contributing",
        "## License"
    ];

    [Fact]
    public void Feature_doc_count_is_at_most_45()
    {
        var featuresDir = Path.Combine(RepositoryScan.RepoRoot, "docs", "features");
        Directory.Exists(featuresDir).ShouldBeTrue("docs/features directory must exist.");

        var featureDocs = Directory.GetFiles(featuresDir, "f-*.md")
            .Select(Path.GetFileName)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        featureDocs.Count.ShouldBeGreaterThan(0);
        featureDocs.Count.ShouldBeLessThanOrEqualTo(
            45,
            $"WP-SIMPL-34: docs/features/f-*.md count ({featureDocs.Count}) must be <= 45.");
    }

    [Fact]
    public void Readme_has_required_sections()
    {
        var readme = RepositoryScan.ReadText("README.md");
        var lineCount = RepositoryScan.CountLines(readme);

        lineCount.ShouldBeLessThanOrEqualTo(
            250,
            $"WP-SIMPL-34: README.md line count ({lineCount}) must be <= 250.");

        foreach (var heading in RequiredReadmeHeadings)
        {
            readme.ShouldContain(heading);
        }

        readme.ShouldNotContain("Feature Inventory", Case.Insensitive);

        var hasEmoji = HasEmojiCodePoint(readme);
        hasEmoji.ShouldBeFalse("README.md must not contain emoji characters.");
    }

    [Fact]
    public void Migration_guide_lists_every_removed_config_key()
    {
        var migrationGuide = RepositoryScan.ReadText("docs/operations/migration-simplification.md");

        var field = typeof(UnknownGatewayKeyGuard).GetField(
            "RemovedSimpl12Prefixes",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

        field.ShouldNotBeNull("UnknownGatewayKeyGuard.RemovedSimpl12Prefixes field must exist.");

        var removedPrefixes = (string[]?)field.GetValue(null);
        removedPrefixes.ShouldNotBeNull();
        removedPrefixes.Length.ShouldBeGreaterThan(0);

        var missing = removedPrefixes
            .Where(key => !migrationGuide.Contains(key, StringComparison.OrdinalIgnoreCase))
            .ToList();

        missing.ShouldBeEmpty(
            $"docs/operations/migration-simplification.md is missing removed config key(s): {string.Join(", ", missing)}");
    }

    private static bool HasEmojiCodePoint(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            if ((v >= 0x1F300 && v <= 0x1FAFF) || (v >= 0x2600 && v <= 0x27BF))
            {
                return true;
            }
        }

        return false;
    }
}
