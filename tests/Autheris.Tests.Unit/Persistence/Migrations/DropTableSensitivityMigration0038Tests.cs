using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

[Trait("Category", "Security")]
public sealed class DropTableSensitivityMigration0038Tests
{
    private const string StepName = "drop_table_sensitivity";

    [Fact]
    public void Migration0038_ExistsPassesStepSqlLintAndDropsSensitivityColumns()
    {
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var steps = SqlMigrationStep.LoadEmbedded(engine);
            var step = steps.Single(s => s.Version == 38 && s.Name == StepName);
            step.Phase3Marker.ShouldBe("MIG-0038");
            StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();
            step.Sql.ShouldContain("sensitivity", Case.Insensitive);
            step.Sql.ShouldContain("requires_four_eyes", Case.Insensitive);

            var shape = GovernanceSchemaShapeLoader.LoadAt(engine, 38, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));
            var tables = shape.Tables.Single(t => string.Equals(t.Name, "TABLES", StringComparison.OrdinalIgnoreCase));
            tables.Columns.ShouldNotContain(c => string.Equals(c.Name, "sensitivity", StringComparison.OrdinalIgnoreCase));
            tables.Columns.ShouldNotContain(c => string.Equals(c.Name, "requires_four_eyes", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Migration0038_ChecksumMatchesGoldenFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 38 && s.Name == StepName);
            var checksumsPath = Path.Combine(dir.FullName, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
            var line = File.ReadAllLines(checksumsPath).Single(l => l.StartsWith("0038 drop_table_sensitivity ", StringComparison.Ordinal));
            step.Checksum.ShouldBe(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]);
        }
    }
}
