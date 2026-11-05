using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

public sealed class DocsAdvisoryMarkerTests
{
    private static readonly Regex AdvisoryRegex = new(
        @"GHSA(-[23456789cfghjmpqrvwx]{4}){3}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MarkerRegex = new(
        @"\b(payload|exploit|PoC|proof of concept|bypass via|root of|root cause|class of|reproduc)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NeutralMethodNameRegex = new(
        @"^Ghsa_[a-z0-9]{4}_[a-z0-9]{4}_regression$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".cs", ".yml", ".yaml", ".json", ".txt", ".sh"
    };

    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git"
    };

    [Fact]
    [Trait("Category", "Security")]
    public void Docs_and_changelog_contain_no_advisory_detail_markers()
    {
        var repoRoot = RepositoryScan.RepoRoot;
        var searchRoots = new[] { "docs", "src", "tests", ".github" };
        var filesToScan = new List<string>();

        foreach (var dir in searchRoots)
        {
            var dirPath = Path.Combine(repoRoot, dir);
            if (Directory.Exists(dirPath))
            {
                CollectFiles(new DirectoryInfo(dirPath), filesToScan);
            }
        }

        var changelogPath = Path.Combine(repoRoot, "CHANGELOG.md");
        if (File.Exists(changelogPath))
        {
            filesToScan.Add(changelogPath);
        }

        filesToScan.ShouldNotBeEmpty("Scanner must find files to inspect.");

        var violations = new List<string>();

        foreach (var file in filesToScan)
        {
            var relPath = RepositoryScan.Normalize(Path.GetRelativePath(repoRoot, file));
            var lines = File.ReadLines(file);
            var lineNumber = 1;

            foreach (var line in lines)
            {
                if (AdvisoryRegex.IsMatch(line) && MarkerRegex.IsMatch(line))
                {
                    // SEC-SIMPL-01: output lists path:line only, never the line text
                    violations.Add($"{relPath}:{lineNumber}");
                }
                lineNumber++;
            }
        }

        violations.ShouldBeEmpty(
            $"Advisory detail markers detected at:\n{string.Join("\n", violations)}");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("GHSA-3953-r994-fg4m: Transport gate", false)]
    [InlineData("GHSA-6523-m29q-c397: Governed aggregation", false)]
    [InlineData("GHSA-3953-r994-fg4m: bypass via parameter injection", true)]
    [InlineData("GHSA-6523-m29q-c397: proof of concept execution", true)]
    [InlineData("GHSA-7ccv-hfgq-5qwm: payload tampering discovered", true)]
    [InlineData("GHSA-7pjw-hcrc-hvff: root cause analysis", true)]
    [InlineData("No advisory here with bypass via header", false)]
    [InlineData("GHSA-xxxx: exploit sample", false)] // not a real 3-block advisory ID
    public void Advisory_detail_marker_detection_theory(string line, bool shouldFail)
    {
        var hasViolation = AdvisoryRegex.IsMatch(line) && MarkerRegex.IsMatch(line);
        hasViolation.ShouldBe(shouldFail);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Advisory_test_names_are_neutral()
    {
        var repoRoot = RepositoryScan.RepoRoot;
        var testsDir = Path.Combine(repoRoot, "tests");
        Directory.Exists(testsDir).ShouldBeTrue();

        var csFiles = new List<string>();
        CollectFiles(new DirectoryInfo(testsDir), csFiles);

        var methodRegex = new Regex(
            @"^\s*(?:(?:public|private|protected|internal|static|async|virtual|override|sealed)\s+)+(?:void|Task(?:<[^>]+>)?|ValueTask(?:<[^>]+>)?|[a-zA-Z0-9_<>?,\[\]]+)\s+([A-Za-z0-9_]+)\s*\(",
            RegexOptions.Multiline);

        var violations = new List<string>();

        foreach (var file in csFiles)
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relPath = RepositoryScan.Normalize(Path.GetRelativePath(repoRoot, file));
            var content = File.ReadAllText(file);

            foreach (Match match in methodRegex.Matches(content))
            {
                var methodName = match.Groups[1].Value;
                if (methodName.Contains("Ghsa", StringComparison.OrdinalIgnoreCase))
                {
                    if (!NeutralMethodNameRegex.IsMatch(methodName))
                    {
                        violations.Add($"{relPath}: method '{methodName}' does not match neutral pattern '^Ghsa_[a-z0-9]{{4}}_[a-z0-9]{{4}}_regression$'");
                    }
                }
            }
        }

        violations.ShouldBeEmpty(
            $"Non-neutral advisory test method names found:\n{string.Join("\n", violations)}");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Ghsa_6523_c397_regression", true)]
    [InlineData("Ghsa_92vp_pp52_regression", true)]
    [InlineData("Ghsa_3953_fg4m_regression", true)]
    [InlineData("GHSA_6523_m29q_c397_RangePredicates", false)]
    [InlineData("Ghsa_something_defect_regression", false)]
    [InlineData("Test_Ghsa_1234_5678_regression", false)]
    public void Advisory_test_name_pattern_theory(string methodName, bool isValid)
    {
        NeutralMethodNameRegex.IsMatch(methodName).ShouldBe(isValid);
    }

    private static void CollectFiles(DirectoryInfo dir, List<string> collected)
    {
        foreach (var file in dir.EnumerateFiles())
        {
            if (AllowedExtensions.Contains(file.Extension))
            {
                collected.Add(file.FullName);
            }
        }

        foreach (var subDir in dir.EnumerateDirectories())
        {
            if (!SkippedDirs.Contains(subDir.Name))
            {
                CollectFiles(subDir, collected);
            }
        }
    }
}
