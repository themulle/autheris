using System.Data;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Catalog;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

[Trait("Category", "Security")]
public sealed class TableObjectKindMigration0030Tests
{
    [Fact]
    public void Migration0030_ExistsPassesStepSqlLintAndAddsAGuardedNotNullColumn()
    {
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var steps = SqlMigrationStep.LoadEmbedded(engine);
            var step = steps.Single(s => s.Version == 30 && s.Name == "table_object_kind");
            StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();
            step.Sql.ShouldContain("object_kind");
            step.Sql.ShouldContain("'table'");
            step.Sql.ShouldContain("CHECK (object_kind IN (");
            step.Sql.ShouldContain(engine == "postgresql" ? "IF NOT EXISTS" : "COL_LENGTH");

            var shape = GovernanceSchemaShapeLoader.LoadAt(engine, 30, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));
            var tables = shape.Tables.Single(t => string.Equals(t.Name, "TABLES", StringComparison.OrdinalIgnoreCase));
            tables.Columns.Single(c => c.Name == "object_kind").Nullable.ShouldBeFalse();
        }
    }

    [Fact]
    public void Migration0030_ChecksumMatchesGoldenFile()
    {
        var repoRoot = FindRepoRoot();
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 30 && s.Name == "table_object_kind");
            var checksumsPath = Path.Combine(repoRoot, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
            var line = File.ReadAllLines(checksumsPath).Single(l => l.StartsWith("0030 table_object_kind ", StringComparison.Ordinal));
            step.Checksum.ShouldBe(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]);
        }
    }

    [Theory]
    [InlineData("view", DiscoveryObjectKind.View)]
    [InlineData("table", DiscoveryObjectKind.Table)]
    [InlineData("VIEW", DiscoveryObjectKind.Table)]
    [InlineData("procedure", DiscoveryObjectKind.Table)]
    [InlineData("materialized_view", DiscoveryObjectKind.Table)]
    [InlineData("", DiscoveryObjectKind.Table)]
    [InlineData(null, DiscoveryObjectKind.Table)]
    public void Hydrator_MapsOnlyAStoredViewToViewAndEverythingElseToTable(string? stored, DiscoveryObjectKind expected)
    {
        using var reader = RowWith(stored).CreateDataReader();
        reader.Read().ShouldBeTrue();

        CatalogRowHydrator.ReadTableEntity(reader).ObjectKind.ShouldBe(expected);
    }

    [Fact]
    public void Hydrator_RowWithoutTheKindColumn_ReadsAsTable()
    {
        var table = RowWith("view");
        table.Columns.Remove("object_kind");
        using var reader = table.CreateDataReader();
        reader.Read().ShouldBeTrue();

        CatalogRowHydrator.ReadTableEntity(reader).ObjectKind.ShouldBe(DiscoveryObjectKind.Table);
    }

    private static DataTable RowWith(string? kind)
    {
        var table = new DataTable();
        (string Name, Type Type)[] columns =
        [
            ("id", typeof(string)), ("source_type", typeof(string)), ("source_name", typeof(string)), ("schema_name", typeof(string)),
            ("table_name", typeof(string)), ("display_name", typeof(string)), ("is_active", typeof(int)), ("data_source_type", typeof(int)),
            ("http_endpoint_json", typeof(string)), ("plugin_name", typeof(string)), ("description", typeof(string)),
            ("long_description", typeof(string)), ("doc_source", typeof(string)), ("required_filter_columns_json", typeof(string)),
            ("texts_json", typeof(string)), ("min_group_size", typeof(int)), ("non_personal", typeof(int)), ("dbt_unique_id", typeof(string)),
            ("object_kind", typeof(string))
        ];
        foreach (var (name, type) in columns)
        {
            table.Columns.Add(name, type);
        }

        var row = table.NewRow();
        row["id"] = Guid.NewGuid().ToString();
        row["source_type"] = "PostgreSql";
        row["source_name"] = "d";
        row["schema_name"] = "s";
        row["table_name"] = "t";
        row["display_name"] = "t";
        row["is_active"] = 1;
        row["data_source_type"] = 0;
        row["object_kind"] = (object?)kind ?? DBNull.Value;
        table.Rows.Add(row);
        return table;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        dir.ShouldNotBeNull("Autheris.sln was not found above test output directory.");
        return dir.FullName;
    }
}
