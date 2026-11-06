namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

public sealed partial class TestLayeringArchitectureTests
{
    private const string BaselineRelativePath = "tests/Autheris.Tests.Architecture/Baselines/unit-db-host-files.txt";

    [GeneratedRegex(@"\b(SqliteConnection|DuckDBConnection|NpgsqlConnection|SqlConnection|TestServer)\b|WebApplication\.Create", RegexOptions.CultureInvariant)]
    private static partial Regex DbOrHostPattern();

    [Fact]
    [Trait("Category", "Security")]
    public void Unit_tests_open_no_database_or_host()
    {
        var root = RepositoryScan.FindRepositoryRoot();
        var baselinePath = Path.Combine(root, BaselineRelativePath.Replace('/', Path.DirectorySeparatorChar));

        var actual = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in RepositoryScan.EnumerateFiles("tests/Autheris.Tests.Unit", "*.cs"))
        {
            var content = RepositoryScan.ReadText(file);
            if (DbOrHostPattern().IsMatch(content))
            {
                actual.Add(RepositoryScan.ToRelativePath(file));
            }
        }

        if (string.Equals(Environment.GetEnvironmentVariable("AUTHERIS_UPDATE_BASELINES"), "1", StringComparison.Ordinal))
        {
            var lines = new List<string> { "# WP-SIMPL-22 baseline: unit test files opening database connections or test hosts (ratchet: shrink-only)" };
            lines.AddRange(actual);
            File.WriteAllLines(baselinePath, lines);
            return;
        }

        File.Exists(baselinePath).ShouldBeTrue($"Missing baseline file: {BaselineRelativePath}");

        var baseline = File.ReadAllLines(baselinePath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

        var added = actual.Where(path => !baseline.Contains(path)).ToList();
        added.ShouldBeEmpty(
            $"WP-SIMPL-22: Unit tests in Autheris.Tests.Unit must not open database connections or hosts. Move real DB/host tests to Autheris.Tests.Integration. New violations: {string.Join(", ", added)}");

        var removed = baseline.Where(path => !actual.Contains(path)).OrderBy(path => path, StringComparer.Ordinal).ToList();
        if (removed.Count > 0)
        {
            Console.WriteLine(
                $"Hint: {removed.Count} baseline entry/entries in {BaselineRelativePath} no longer open DB/host connections and can be pruned: {string.Join(", ", removed)}");
        }
    }
}
