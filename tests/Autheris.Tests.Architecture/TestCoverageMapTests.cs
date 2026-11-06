namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

internal static partial class TestInventoryScanner
{
    [GeneratedRegex(@"^\s*namespace\s+([A-Za-z0-9_.]+)\s*[;{]", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex NamespaceRegex();

    [GeneratedRegex(@"^\s*(?:public|internal|private|protected)\s+(?:(?:sealed|abstract|static|partial)\s+)*(?:class|record)\s+([A-Za-z0-9_]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TypeDeclRegex();

    [GeneratedRegex(@"\[\s*(?:Fact|Theory|ParityFact|ParityTheory|SparkFact|SparkTheory|RequiresContainerRuntimeFact|RequiresContainerRuntimeTheory|Property)(?:Attribute)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex FactAttrRegex();

    [GeneratedRegex(@"\[\s*Trait(?:Attribute)?\s*\(\s*""Category""\s*,\s*""Security""\s*\)\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex SecurityTraitRegex();

    [GeneratedRegex(@"^\s*(?:public|internal|private|protected)\s+(?:(?:static|async|virtual|override|sealed)\s+)*[A-Za-z0-9_<>,.?\[\]\s]+\s+([A-Za-z0-9_]+)\s*\(", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex MethodDeclRegex();

    public static (HashSet<string> AllTests, HashSet<string> SecurityTests) ScanRepository()
    {
        var allTests = new HashSet<string>(StringComparer.Ordinal);
        var securityTests = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in RepositoryScan.EnumerateFiles("tests", "*.cs"))
        {
            var source = RepositoryScan.ReadText(file);
            ExtractFromSource(source, allTests, securityTests);
        }

        return (allTests, securityTests);
    }

    public static void ExtractFromSource(string source, HashSet<string> allTests, HashSet<string> securityTests)
    {
        var nsMatch = NamespaceRegex().Match(source);
        var ns = nsMatch.Success ? nsMatch.Groups[1].Value : "Global";

        var typeMatches = TypeDeclRegex().Matches(source);
        if (typeMatches.Count == 0)
        {
            return;
        }

        for (int i = 0; i < typeMatches.Count; i++)
        {
            var tMatch = typeMatches[i];
            var className = tMatch.Groups[1].Value;
            int classStart = tMatch.Index;
            int bodyEnd = i + 1 < typeMatches.Count ? typeMatches[i + 1].Index : source.Length;

            int prefixStart = i > 0 ? typeMatches[i - 1].Index + typeMatches[i - 1].Length : 0;
            int windowStart = Math.Max(prefixStart, classStart - 600);
            var classAttrWindow = source.Substring(windowStart, classStart - windowStart);
            int lastSep = Math.Max(classAttrWindow.LastIndexOf('}'), classAttrWindow.LastIndexOf(';'));
            if (lastSep >= 0)
            {
                classAttrWindow = classAttrWindow[(lastSep + 1)..];
            }

            bool classIsSecurity = SecurityTraitRegex().IsMatch(classAttrWindow);

            int bodyStart = tMatch.Index + tMatch.Length;
            if (bodyStart >= bodyEnd)
            {
                continue;
            }

            var classBody = source.Substring(bodyStart, bodyEnd - bodyStart);
            var methodMatches = MethodDeclRegex().Matches(classBody);
            foreach (Match mMatch in methodMatches)
            {
                var methodName = mMatch.Groups[1].Value;
                if (string.Equals(methodName, className, StringComparison.Ordinal))
                {
                    continue;
                }

                int mWindowStart = Math.Max(0, mMatch.Index - 600);
                var attrWindow = classBody.Substring(mWindowStart, mMatch.Index - mWindowStart);
                int lastMethodSep = Math.Max(
                    attrWindow.LastIndexOf('}'),
                    Math.Max(attrWindow.LastIndexOf(';'), attrWindow.LastIndexOf('{')));
                if (lastMethodSep >= 0)
                {
                    attrWindow = attrWindow[(lastMethodSep + 1)..];
                }

                if (!FactAttrRegex().IsMatch(attrWindow))
                {
                    continue;
                }

                var fqn = $"{ns}.{className}.{methodName}";
                allTests.Add(fqn);
                if (classIsSecurity || SecurityTraitRegex().IsMatch(attrWindow))
                {
                    securityTests.Add(fqn);
                }
            }
        }
    }

    public static Dictionary<string, (string NewIdOrReason, string Category)> LoadCoverageMap(string root)
    {
        var mapPath = Path.Combine(root, "docs", "plans", "2026-11-05-test-coverage-map.csv");
        var result = new Dictionary<string, (string NewIdOrReason, string Category)>(StringComparer.Ordinal);
        if (!File.Exists(mapPath))
        {
            return result;
        }

        var lines = File.ReadAllLines(mapPath);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(',', 3);
            if (parts.Length >= 2)
            {
                var oldId = parts[0].Trim();
                var newIdOrReason = parts[1].Trim();
                var category = parts.Length >= 3 ? parts[2].Trim() : string.Empty;
                if (oldId.Length > 0)
                {
                    result[oldId] = (newIdOrReason, category);
                }
            }
        }

        return result;
    }
}

public sealed partial class TestCoverageMapTests
{
    private const string BaselineRelativePath = "tests/Autheris.Tests.Architecture/Baselines/security-tests-base.txt";
    private const string CoverageMapRelativePath = "docs/plans/2026-11-05-test-coverage-map.csv";

    [GeneratedRegex(@"WP-SIMPL-\d+", RegexOptions.CultureInvariant)]
    private static partial Regex DeletingWpRegex();

    [Fact]
    [Trait("Category", "Security")]
    public void No_security_test_lost_without_mapping()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var baselinePath = Path.Combine(root, BaselineRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(baselinePath).ShouldBeTrue($"Missing baseline file: {BaselineRelativePath}");

        var (_, currentSecurityTests) = TestInventoryScanner.ScanRepository();
        var coverageMap = TestInventoryScanner.LoadCoverageMap(root);

        var baseline = File.ReadAllLines(baselinePath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

        var unmappedMissing = baseline
            .Where(testId => !currentSecurityTests.Contains(testId) && !coverageMap.ContainsKey(testId))
            .ToList();

        unmappedMissing.ShouldBeEmpty(
            $"WP-SIMPL-23: Security tests from {BaselineRelativePath} were removed or renamed without an entry in {CoverageMapRelativePath}: {string.Join(", ", unmappedMissing.Take(20))}");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Map_rows_reference_existing_tests_or_deleting_wp()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var mapPath = Path.Combine(root, CoverageMapRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(mapPath).ShouldBeTrue($"Missing coverage map file: {CoverageMapRelativePath}");

        var (allTests, _) = TestInventoryScanner.ScanRepository();
        var coverageMap = TestInventoryScanner.LoadCoverageMap(root);

        var invalidRows = new List<string>();
        foreach (var (oldId, (newIdOrReason, _)) in coverageMap)
        {
            if (string.IsNullOrWhiteSpace(newIdOrReason))
            {
                invalidRows.Add($"{oldId} -> <empty>");
                continue;
            }

            var candidateTestId = newIdOrReason.Split(' ', 2)[0].Trim();
            bool referencesExistingTest = allTests.Contains(newIdOrReason) || allTests.Contains(candidateTestId);
            bool referencesDeletingWp = DeletingWpRegex().IsMatch(newIdOrReason);

            if (!referencesExistingTest && !referencesDeletingWp)
            {
                invalidRows.Add($"{oldId} -> {newIdOrReason}");
            }
        }

        invalidRows.ShouldBeEmpty(
            $"WP-SIMPL-23: Every row in {CoverageMapRelativePath} must reference an existing test method or a WP-SIMPL-* deletion work package. Invalid rows: {string.Join("; ", invalidRows.Take(20))}");
    }
}
