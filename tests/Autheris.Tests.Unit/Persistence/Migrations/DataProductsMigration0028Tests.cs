using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

[Trait("Category", "Security")]
public sealed class DataProductsMigration0028Tests
{
    private static readonly string[] Tables = ["DATA_PRODUCTS", "DATA_PRODUCT_VERSIONS", "DATA_SOURCE_OWNERS"];

    [Fact]
    public void Migration0028_ExistsPassesStepSqlLintAndKeepsTheTenantInEveryKey()
    {
        var registered = Enum.GetValues<GovernanceTable>().Select(t => t.ToSqlName()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var table in new[] { "DATA_PRODUCTS", "DATA_PRODUCT_VERSIONS" })
        {
            registered.ShouldContain(table);
            GovernanceGlobalTables.IsGlobal(table).ShouldBeFalse($"{table} is tenant-scoped");
        }

        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var steps = SqlMigrationStep.LoadEmbedded(engine);
            var step = steps.Single(s => s.Version == 28 && s.Name == "data_products");
            step.Phase3Marker.ShouldBe("MIG-0028");
            StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();
            step.Sql.ShouldNotContain("REFERENCES", Case.Insensitive, "no foreign key to source systems");

            var shape = GovernanceSchemaShapeLoader.LoadAt(engine, 28, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));
            foreach (var table in Tables)
            {
                var shaped = shape.Tables.Single(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase));
                shaped.PrimaryKey[0].ShouldBe("tenant_id");
                shaped.Columns.Single(c => c.Name == "tenant_id").Nullable.ShouldBeFalse();
            }

            var products = shape.Tables.Single(t => string.Equals(t.Name, "DATA_PRODUCTS", StringComparison.OrdinalIgnoreCase));
            products.PrimaryKey.ShouldBe(["tenant_id", "product_schema", "product_name"]);
            products.Indexes.ShouldContain(i => i.Unique && i.Columns.SequenceEqual(new[] { "tenant_id", "id" }, StringComparer.OrdinalIgnoreCase));
            var owners = shape.Tables.Single(t => string.Equals(t.Name, "DATA_SOURCE_OWNERS", StringComparison.OrdinalIgnoreCase));
            owners.PrimaryKey.ShouldBe(["tenant_id", "data_source", "owner_sid"]);
        }
    }

    [Fact]
    public void Migration0028_ChecksumMatchesGoldenFile()
    {
        var repoRoot = FindRepoRoot();
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 28 && s.Name == "data_products");
            var checksumsPath = Path.Combine(repoRoot, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
            var line = File.ReadAllLines(checksumsPath).Single(l => l.StartsWith("0028 data_products ", StringComparison.Ordinal));
            step.Checksum.ShouldBe(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        dir.ShouldNotBeNull("Autheris.sln was not found above test output directory.");
        return dir.FullName;
    }
}
