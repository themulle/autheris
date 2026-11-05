namespace Autheris.Tests.Architecture;

using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

public sealed class MeasurementScriptTests
{
    private static readonly Regex ResultFileNameRegex = new(
        @"^[a-z-]+-\d{4}-\d{2}-\d{2}-[0-9a-f]{7}\.json$",
        RegexOptions.Compiled);

    [Fact]
    public void Results_files_follow_naming_scheme()
    {
        var resultsDir = Path.Combine(RepositoryScan.RepoRoot, "benchmarks", "results");
        if (!Directory.Exists(resultsDir))
        {
            return;
        }

        var jsonFiles = Directory.GetFiles(resultsDir, "*.json");
        foreach (var file in jsonFiles)
        {
            var fileName = Path.GetFileName(file);
            ResultFileNameRegex.IsMatch(fileName).ShouldBeTrue(
                $"File '{fileName}' does not match pattern '^[a-z-]+-\\d{{4}}-\\d{{2}}-\\d{{2}}-[0-9a-f]{{7}}\\.json$'");

            var content = File.ReadAllText(file);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            root.TryGetProperty("metric", out var metricProp).ShouldBeTrue($"File '{fileName}' missing 'metric' property");
            metricProp.GetString().ShouldNotBeNullOrWhiteSpace();

            root.TryGetProperty("sha", out var shaProp).ShouldBeTrue($"File '{fileName}' missing 'sha' property");
            shaProp.GetString().ShouldNotBeNullOrWhiteSpace();

            root.TryGetProperty("values", out var valuesProp).ShouldBeTrue($"File '{fileName}' missing 'values' property");
            valuesProp.ValueKind.ShouldBe(JsonValueKind.Object);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Scripts_contain_no_tokens()
    {
        var scriptsDir = Path.Combine(RepositoryScan.RepoRoot, "benchmarks", "scripts");
        if (!Directory.Exists(scriptsDir))
        {
            return;
        }

        var forbiddenTokenPatterns = new[]
        {
            @"\bghp_[A-Za-z0-9_]{36,}\b",
            @"\bgithub_pat_[A-Za-z0-9_]{82,}\b",
            @"Authorization:\s*(Bearer|token)\s+[A-Za-z0-9._-]{10,}"
        };

        var scriptFiles = Directory.GetFiles(scriptsDir, "*.sh");
        foreach (var file in scriptFiles)
        {
            var text = File.ReadAllText(file);
            foreach (var pattern in forbiddenTokenPatterns)
            {
                var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
                match.Success.ShouldBeFalse(
                    $"Script '{Path.GetFileName(file)}' contains potential secret/token matching '{pattern}'");
            }
        }
    }

    [Fact]
    public void Repo_metrics_map_regex_matches_ratchet_regex()
    {
        var scriptPath = Path.Combine(RepositoryScan.RepoRoot, "benchmarks", "scripts", "repo-metrics.sh");
        File.Exists(scriptPath).ShouldBeTrue("benchmarks/scripts/repo-metrics.sh must exist");

        var scriptContent = File.ReadAllText(scriptPath);
        const string expectedRegex = @"\.Map(Get|Post|Put|Delete|Patch|Methods|Group|Hub|GraphQL)\s*\(";
        scriptContent.ShouldContain(expectedRegex, Case.Sensitive);

        var ratchetPath = Path.Combine(RepositoryScan.RepoRoot, "tests", "Autheris.Tests.Architecture", "ProgramRatchetTests.cs");
        if (File.Exists(ratchetPath))
        {
            var ratchetContent = File.ReadAllText(ratchetPath);
            ratchetContent.ShouldContain(expectedRegex, Case.Sensitive);
        }
    }
}
