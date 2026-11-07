using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

public sealed class ConfigurationReferenceTests
{
    private static readonly string ReferenceFilePath = Path.Combine(RepositoryScan.RepoRoot, "docs", "reference", "configuration.md");

    [Fact]
    public void Reference_is_current()
    {
        var generated = ConfigurationReferenceGenerator.GenerateMarkdown();
        var updateEnv = Environment.GetEnvironmentVariable("AUTHERIS_UPDATE_CONFIG_REFERENCE");
        if (string.Equals(updateEnv, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(updateEnv, "true", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReferenceFilePath)!);
            File.WriteAllText(ReferenceFilePath, generated.Replace("\r\n", "\n"));
        }

        File.Exists(ReferenceFilePath).ShouldBeTrue(
            $"Checked-in configuration reference missing at {ReferenceFilePath}. Set AUTHERIS_UPDATE_CONFIG_REFERENCE=1 to generate.");

        var checkedIn = File.ReadAllText(ReferenceFilePath).Replace("\r\n", "\n");
        var normalizedGenerated = generated.Replace("\r\n", "\n");

        if (!string.Equals(normalizedGenerated, checkedIn, StringComparison.Ordinal))
        {
            var genLines = normalizedGenerated.Split('\n');
            var chkLines = checkedIn.Split('\n');
            var max = Math.Min(genLines.Length, chkLines.Length);
            for (var i = 0; i < max; i++)
            {
                if (genLines[i] != chkLines[i])
                {
                    Assert.Fail($"docs/reference/configuration.md diff at line {i + 1}:\nGenerated: '{genLines[i]}'\nCheckedIn: '{chkLines[i]}'\nSet AUTHERIS_UPDATE_CONFIG_REFERENCE=1 to regenerate.");
                }
            }
            if (genLines.Length != chkLines.Length)
            {
                Assert.Fail($"docs/reference/configuration.md line count mismatch: generated {genLines.Length}, checkedIn {chkLines.Length}. Set AUTHERIS_UPDATE_CONFIG_REFERENCE=1 to regenerate.");
            }
        }

        normalizedGenerated.ShouldBe(
            checkedIn,
            "Checked-in configuration reference docs/reference/configuration.md is out of date. Set AUTHERIS_UPDATE_CONFIG_REFERENCE=1 to regenerate.");
    }

    [Fact]
    public void Every_option_has_a_description()
    {
        var rows = ConfigurationReferenceGenerator.CollectAllRows();
        rows.Count.ShouldBeGreaterThan(0, "Configuration reference generator found 0 options rows.");

        var missing = rows.Where(r => string.IsNullOrWhiteSpace(r.Description)).ToList();
        missing.ShouldBeEmpty(
            $"Found {missing.Count} options rows with empty description: {string.Join(", ", missing.Select(m => m.Key))}");
    }
}
