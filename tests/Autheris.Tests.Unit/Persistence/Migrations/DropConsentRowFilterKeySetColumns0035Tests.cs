using System.Text.RegularExpressions;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

[Trait("Category", "Security")]
public sealed class DropConsentRowFilterKeySetColumns0035Tests
{
    private static readonly string[] DroppedColumns =
    [
        "key_set_strategy",
        "key_set_duration_seconds",
        "key_set_max_keys"
    ];

    [Fact]
    public void Migration0035_ExistsPassesStepSqlLintAndDropsKeySetColumns()
    {
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var steps = SqlMigrationStep.LoadEmbedded(engine);
            var step = steps.Single(s => s.Version == 35 && s.Name == "drop_consent_row_filter_key_set_columns");
            StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();

            foreach (var column in DroppedColumns)
            {
                step.Sql.ShouldContain(column);
            }

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

            var shape = GovernanceSchemaShapeLoader.LoadAt(engine, 35, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));
            var crfTable = shape.Tables.Single(t => string.Equals(t.Name, "CONSENT_ROW_FILTERS", StringComparison.OrdinalIgnoreCase));
            foreach (var column in DroppedColumns)
            {
                crfTable.Columns.ShouldNotContain(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public void Migration0035_ChecksumMatchesGoldenFile()
    {
        var repoRoot = FindRepoRoot();
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 35 && s.Name == "drop_consent_row_filter_key_set_columns");
            var checksumsPath = Path.Combine(repoRoot, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
            var line = File.ReadAllLines(checksumsPath).Single(l => l.StartsWith("0035 drop_consent_row_filter_key_set_columns ", StringComparison.Ordinal));
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
