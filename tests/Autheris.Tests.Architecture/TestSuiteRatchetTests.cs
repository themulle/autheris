namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

public sealed partial class TestSuiteRatchetTests
{
    private const int DefaultTestFileLineCeiling = 800;
    private const string UnitRootBaselinePath = "tests/Autheris.Tests.Architecture/Baselines/unit-root-files.txt";
    private const string LineCeilingsBaselinePath = "tests/Autheris.Tests.Architecture/Baselines/test-file-line-ceilings.txt";
    private const string MockCountsBaselinePath = "tests/Autheris.Tests.Architecture/Baselines/mock-counts.txt";
    private const string UnitFoldersBaselinePath = "tests/Autheris.Tests.Architecture/Baselines/unit-test-folders.txt";
    private const string SecurityTestsBaselinePath = "tests/Autheris.Tests.Architecture/Baselines/security-tests-base.txt";

    [GeneratedRegex(@"\bSubstitute\.For\b", RegexOptions.CultureInvariant)]
    private static partial Regex SubstituteForRegex();

    [GeneratedRegex(@"\.Received\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex ReceivedRegex();

    [Fact]
    [Trait("Category", "Security")]
    public void No_test_files_in_unit_root()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var unitRootDir = Path.Combine(root, "tests", "Autheris.Tests.Unit");
        var baselineFile = Path.Combine(root, UnitRootBaselinePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(baselineFile).ShouldBeTrue($"Missing baseline file: {UnitRootBaselinePath}");

        var baseline = File.ReadAllLines(baselineFile)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

        var actual = Directory.EnumerateFiles(unitRootDir, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(RepositoryScan.ToRelativePath)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var added = actual.Where(path => !baseline.Contains(path)).ToList();
        added.ShouldBeEmpty(
            $"WP-SIMPL-23: Do not add new test files directly under tests/Autheris.Tests.Unit/ root; place them in a domain subfolder. New root files: {string.Join(", ", added)}");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Test_file_line_ceiling()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var baselineFile = Path.Combine(root, LineCeilingsBaselinePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(baselineFile).ShouldBeTrue($"Missing baseline file: {LineCeilingsBaselinePath}");

        var ceilings = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(baselineFile))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('\t');
            parts.Length.ShouldBe(2, $"Malformed line in {LineCeilingsBaselinePath}: '{raw}'");
            ceilings[parts[0]] = int.Parse(parts[1], CultureInfo.InvariantCulture);
        }

        var violations = new List<string>();
        foreach (var file in RepositoryScan.EnumerateFiles("tests", "*.cs"))
        {
            var relative = RepositoryScan.ToRelativePath(file);
            var lineCount = CountLines(Path.Combine(root, file));
            var allowedCeiling = ceilings.TryGetValue(relative, out var recordedCeiling)
                ? recordedCeiling
                : DefaultTestFileLineCeiling;

            if (lineCount > allowedCeiling)
            {
                violations.Add($"{relative} has {lineCount} lines (ceiling: {allowedCeiling})");
            }
        }

        violations.ShouldBeEmpty(
            $"WP-SIMPL-23: Test files must not exceed {DefaultTestFileLineCeiling} lines (or their recorded ratchet ceiling in {LineCeilingsBaselinePath}): {string.Join("; ", violations)}");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Substitute_for_count_does_not_grow()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var ceilings = LoadMockCountCeilings(root);
        ceilings.TryGetValue("Substitute.For", out var maxAllowed).ShouldBeTrue();

        int actual = 0;
        foreach (var file in RepositoryScan.EnumerateFiles("tests", "*.cs"))
        {
            var relative = RepositoryScan.ToRelativePath(file);
            if (relative.EndsWith("TestSuiteRatchetTests.cs", StringComparison.Ordinal))
            {
                continue;
            }

            actual += SubstituteForRegex().Count(RepositoryScan.ReadText(file));
        }

        actual.ShouldBeLessThanOrEqualTo(
            maxAllowed,
            $"WP-SIMPL-23: Total Substitute.For count across tests/ ({actual}) exceeded the ratchet ceiling ({maxAllowed}) in {MockCountsBaselinePath}.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Received_count_does_not_grow()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var ceilings = LoadMockCountCeilings(root);
        ceilings.TryGetValue("Received", out var maxAllowed).ShouldBeTrue();

        int actual = 0;
        foreach (var file in RepositoryScan.EnumerateFiles("tests", "*.cs"))
        {
            var relative = RepositoryScan.ToRelativePath(file);
            if (relative.EndsWith("TestSuiteRatchetTests.cs", StringComparison.Ordinal))
            {
                continue;
            }

            actual += ReceivedRegex().Count(RepositoryScan.ReadText(file));
        }

        actual.ShouldBeLessThanOrEqualTo(
            maxAllowed,
            $"WP-SIMPL-23: Total .Received(...) interaction assertion count across tests/ ({actual}) exceeded the ratchet ceiling ({maxAllowed}) in {MockCountsBaselinePath}.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Test_folders_follow_domains()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var baselineFile = Path.Combine(root, UnitFoldersBaselinePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(baselineFile).ShouldBeTrue($"Missing baseline file: {UnitFoldersBaselinePath}");

        var allowedFolders = File.ReadAllLines(baselineFile)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

        var unitDir = Path.Combine(root, "tests", "Autheris.Tests.Unit");
        var actualFolders = Directory.EnumerateDirectories(unitDir, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name) &&
                           !string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(name, "TestResults", StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(name, "StrykerOutput", StringComparison.OrdinalIgnoreCase))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var unexpected = actualFolders.Where(folder => !allowedFolders.Contains(folder)).ToList();
        unexpected.ShouldBeEmpty(
            $"WP-SIMPL-23: Top-level folders in tests/Autheris.Tests.Unit must follow domain folders listed in {UnitFoldersBaselinePath}. Unexpected folders: {string.Join(", ", unexpected)}");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Security_category_count_does_not_decrease()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var baselineFile = Path.Combine(root, SecurityTestsBaselinePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(baselineFile).ShouldBeTrue($"Missing baseline file: {SecurityTestsBaselinePath}");

        var baseline = File.ReadAllLines(baselineFile)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

        var (_, currentSecurityTests) = TestInventoryScanner.ScanRepository();
        var coverageMap = TestInventoryScanner.LoadCoverageMap(root);

        int mappedRemovedCount = baseline.Count(id => !currentSecurityTests.Contains(id) && coverageMap.ContainsKey(id));
        int effectiveCount = currentSecurityTests.Count + mappedRemovedCount;

        effectiveCount.ShouldBeGreaterThanOrEqualTo(
            baseline.Count,
            $"WP-SIMPL-23: Effective [Trait(\"Category\", \"Security\")] test count ({effectiveCount}: {currentSecurityTests.Count} active + {mappedRemovedCount} mapped in coverage map) fell below baseline ({baseline.Count}).");
    }

    private static Dictionary<string, int> LoadMockCountCeilings(string root)
    {
        var path = Path.Combine(root, MockCountsBaselinePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Missing baseline file: {MockCountsBaselinePath}");

        var dict = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('\t');
            parts.Length.ShouldBe(2, $"Malformed line in {MockCountsBaselinePath}: '{raw}'");
            dict[parts[0]] = int.Parse(parts[1], CultureInfo.InvariantCulture);
        }

        return dict;
    }

    private static int CountLines(string path)
    {
        using var stream = File.OpenRead(path);
        int newlines = 0;
        int lastByte = -1;
        long totalBytes = 0;
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            totalBytes += read;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    newlines++;
                }
            }

            lastByte = buffer[read - 1];
        }

        if (totalBytes == 0)
        {
            return 0;
        }

        return lastByte == (byte)'\n' ? newlines : newlines + 1;
    }
}
