using System.Text.RegularExpressions;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

[Trait("Category", "Security")]
public sealed class VirtualFilterKeySetMaxKeysMigration0034Tests
{
    [Fact]
    public void Migration0034_ExistsPassesStepSqlLintAndAddsNullableKeySetMaxKeys()
    {
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var steps = SqlMigrationStep.LoadEmbedded(engine);
            var step = steps.Single(s => s.Version == 34 && s.Name == "virtual_filter_key_set_max_keys");
            StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();
            step.Sql.ShouldContain("key_set_max_keys");

            var nonCommentLines = step.Sql
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.StartsWith("--", StringComparison.Ordinal))
                .ToArray();
            foreach (var line in nonCommentLines)
            {
                if (Regex.IsMatch(line, @"\bALTER\b", RegexOptions.IgnoreCase))
                {
                    Regex.IsMatch(line, @"\bALTER\s+TABLE\s+", RegexOptions.IgnoreCase).ShouldBeTrue($"Line must use 'ALTER TABLE ': {line}");
                }

                Regex.IsMatch(line, @"\bALTER\s+(?!TABLE\b)", RegexOptions.IgnoreCase).ShouldBeFalse($"Invalid ALTER target in: {line}");
            }

            var shape = GovernanceSchemaShapeLoader.LoadAt(engine, 34, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));
            var vfTable = shape.Tables.Single(t => string.Equals(t.Name, "VIRTUAL_FILTERS", StringComparison.OrdinalIgnoreCase));
            var col = vfTable.Columns.Single(c => c.Name == "key_set_max_keys");
            col.Nullable.ShouldBeTrue();
            col.Family.ShouldBe("integer");
        }
    }

    [Fact]
    public void Migration0034_ChecksumMatchesGoldenFile()
    {
        var repoRoot = FindRepoRoot();
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 34 && s.Name == "virtual_filter_key_set_max_keys");
            var checksumsPath = Path.Combine(repoRoot, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
            var line = File.ReadAllLines(checksumsPath).Single(l => l.StartsWith("0034 virtual_filter_key_set_max_keys ", StringComparison.Ordinal));
            step.Checksum.ShouldBe(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Autheris.sln was not found above the test output directory.");
        return dir.FullName;
    }
}
