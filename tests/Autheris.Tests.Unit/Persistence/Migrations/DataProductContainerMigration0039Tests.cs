using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

[Trait("Category", "Security")]
public sealed class DataProductContainerMigration0039Tests
{
    private const string StepName = "data_product_container";

    [Fact]
    public void Migration0039_ExistsPassesStepSqlLintAndPromotesDataProductToContainer()
    {
        var registered = Enum.GetValues<GovernanceTable>().Select(t => t.ToSqlName()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        registered.ShouldNotContain("DATA_SOURCE_OWNERS");

        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var steps = SqlMigrationStep.LoadEmbedded(engine);
            var step = steps.Single(s => s.Version == 39 && s.Name == StepName);
            step.Phase3Marker.ShouldBe("MIG-0039");
            StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();
            step.Sql.ShouldContain("owner_group_sid", Case.Insensitive);
            step.Sql.ShouldContain("default_class", Case.Insensitive);
            step.Sql.ShouldContain("visibility", Case.Insensitive);
            step.Sql.ShouldContain("data_source_owners", Case.Insensitive);

            var shape = GovernanceSchemaShapeLoader.LoadAt(engine, 39, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));
            shape.Tables.ShouldNotContain(t => string.Equals(t.Name, "DATA_SOURCE_OWNERS", StringComparison.OrdinalIgnoreCase));

            var products = shape.Tables.Single(t => string.Equals(t.Name, "DATA_PRODUCTS", StringComparison.OrdinalIgnoreCase));
            products.Columns.ShouldContain(c => string.Equals(c.Name, "owner_group_sid", StringComparison.OrdinalIgnoreCase) && c.Nullable);
            products.Columns.ShouldContain(c => string.Equals(c.Name, "default_class", StringComparison.OrdinalIgnoreCase) && c.Nullable);
            products.Columns.ShouldContain(c => string.Equals(c.Name, "visibility", StringComparison.OrdinalIgnoreCase) && !c.Nullable);
            products.Columns.ShouldNotContain(c => string.Equals(c.Name, "product_owner", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Migration0039_ChecksumMatchesGoldenFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 39 && s.Name == StepName);
            var checksumsPath = Path.Combine(dir.FullName, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
            var line = File.ReadAllLines(checksumsPath).Single(l => l.StartsWith("0039 data_product_container ", StringComparison.Ordinal));
            step.Checksum.ShouldBe(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]);
        }
    }
}
