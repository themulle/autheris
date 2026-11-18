using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

[Trait("Category", "Security")]
public sealed class GoldenQueriesMigration0025Tests
{
    [Fact]
    public void Migration0025_ExistsAndPassesStepSqlLintAndShapeContract()
    {
        Enum.GetValues<GovernanceTable>().Select(t => t.ToSqlName()).ShouldContain("GOLDEN_QUERIES");

        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var steps = SqlMigrationStep.LoadEmbedded(engine);
            var step = steps.Single(s => s.Version == 25 && s.Name == "golden_queries");
            step.Phase3Marker.ShouldBe("MIG-0025");
            StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();

            var shape = GovernanceSchemaShapeLoader.LoadAt(engine, 25, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));
            var table = shape.Tables.Single(t => string.Equals(t.Name, "GOLDEN_QUERIES", StringComparison.OrdinalIgnoreCase));
            table.PrimaryKey.ShouldBe(["id"]);
            table.Columns.Single(c => c.Name == "tenant_id").Nullable.ShouldBeFalse();
            table.Columns.Single(c => c.Name == "source_key").Nullable.ShouldBeFalse();
            table.Indexes.Any(i => i.Unique && i.Columns.SequenceEqual(["tenant_id", "table_id", "source", "source_key"], StringComparer.OrdinalIgnoreCase)).ShouldBeTrue();
        }
    }

    [Fact]
    public void Migration0025_ChecksumMatchesGoldenFile()
    {
        var repoRoot = FindRepoRoot();
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 25 && s.Name == "golden_queries");
            var checksumsPath = Path.Combine(repoRoot, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
            var line = File.ReadAllLines(checksumsPath).Single(l => l.StartsWith("0025 golden_queries ", StringComparison.Ordinal));
            step.Checksum.ShouldBe(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]);
        }
    }

    [Fact]
    public void Migration0025_TableIsTenantScoped()
    {
        GovernanceGlobalTables.IsGlobal("GOLDEN_QUERIES").ShouldBeFalse();
        Should.Throw<ArgumentException>(() => TenantScope.RequireTenantString(null, "GOLDEN_QUERIES"));
        Should.Throw<ArgumentException>(() => TenantScope.RequireTenantString(" ", "GOLDEN_QUERIES"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        dir.ShouldNotBeNull("Autheris.sln was not found above test output directory.");
        return dir.FullName;
    }
}
