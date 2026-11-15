using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

[Trait("Category", "Security")]
public sealed class PatternGrantsMigration0019Tests
{
    [Fact]
    public void Migration_0019_Passes_StepSqlLint_And_Matches_ShapeJson()
    {
        var repoRoot = FindRepoRoot();
        var registeredNames = Enum.GetValues<GovernanceTable>()
            .Select(t => t.ToSqlName())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        registeredNames.ShouldContain("CONSENT_APPROVING_OWNERS");

        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var steps = SqlMigrationStep.LoadEmbedded(engine);
            var step = steps.Single(s => s.Version == 19 && s.Name == "pattern_grants");
            step.Phase3Marker.ShouldBe("MIG-0019");

            StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();

            var checksumsPath = Path.Combine(
                repoRoot, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
            var recordedLine = File.ReadAllLines(checksumsPath)
                .Single(l => l.StartsWith("0019 pattern_grants ", StringComparison.Ordinal));
            var recordedSha = recordedLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2];
            step.Checksum.ShouldBe(recordedSha);

            var shape = GovernanceSchemaShapeLoader.LoadAt(
                engine,
                19,
                steps.ToDictionary(s => s.Version, s => s.ShapeDelta));

            var consents = shape.Tables.Single(
                t => string.Equals(t.Name, "CONSENTS", StringComparison.OrdinalIgnoreCase));
            var tableIdColumn = consents.Columns.Single(
                c => string.Equals(c.Name, "table_id", StringComparison.OrdinalIgnoreCase));
            tableIdColumn.Nullable.ShouldBeTrue();

            foreach (var colName in new[] { "object_pattern", "data_source", "column_set_mode", "workflow_mode", "profile_item_no" })
            {
                consents.Columns.Any(c => string.Equals(c.Name, colName, StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();
            }

            var approvingOwners = shape.Tables.Single(
                t => string.Equals(t.Name, "CONSENT_APPROVING_OWNERS", StringComparison.OrdinalIgnoreCase));
            approvingOwners.PrimaryKey.ShouldBe(["tenant_id", "consent_id", "owner_sid_norm"]);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Autheris.sln was not found above test output directory.");
        return dir.FullName;
    }
}
