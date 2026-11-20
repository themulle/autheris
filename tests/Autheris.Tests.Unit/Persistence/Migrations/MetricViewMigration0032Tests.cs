using System.Data;
using System.Text.RegularExpressions;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Catalog;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

[Trait("Category", "Security")]
public sealed class MetricViewMigration0032Tests
{
    [Fact]
    public void Migration0032_ExistsPassesStepSqlLintAndAddsNullableColumnRole()
    {
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var steps = SqlMigrationStep.LoadEmbedded(engine);
            var step = steps.Single(s => s.Version == 32 && s.Name == "metric_view_kind_and_column_role");
            StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();
            step.Sql.ShouldContain("metric_view");
            step.Sql.ShouldContain("column_role");
            step.Sql.ShouldContain("dimension");
            step.Sql.ShouldContain("measure");

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

            var shape = GovernanceSchemaShapeLoader.LoadAt(engine, 32, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));
            var tablesTable = shape.Tables.Single(t => string.Equals(t.Name, "TABLES", StringComparison.OrdinalIgnoreCase));
            tablesTable.Columns.Single(c => c.Name == "metric_view_count_measure").Nullable.ShouldBeTrue();
            var columnsTable = shape.Tables.Single(t => string.Equals(t.Name, "TABLE_COLUMNS", StringComparison.OrdinalIgnoreCase));
            columnsTable.Columns.Single(c => c.Name == "column_role").Nullable.ShouldBeTrue();
        }
    }

    [Fact]
    public void Migration0032_ChecksumMatchesGoldenFile()
    {
        var repoRoot = FindRepoRoot();
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 32 && s.Name == "metric_view_kind_and_column_role");
            var checksumsPath = Path.Combine(repoRoot, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
            var line = File.ReadAllLines(checksumsPath).Single(l => l.StartsWith("0032 metric_view_kind_and_column_role ", StringComparison.Ordinal));
            step.Checksum.ShouldBe(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]);
        }
    }

    [Fact]
    public void Hydrator_MapsMetricViewKind()
    {
        var table = new DataTable();
        (string Name, Type Type)[] columns =
        [
            ("id", typeof(string)), ("source_type", typeof(string)), ("source_name", typeof(string)), ("schema_name", typeof(string)),
            ("table_name", typeof(string)), ("display_name", typeof(string)), ("sensitivity", typeof(string)),
            ("requires_four_eyes", typeof(int)), ("is_active", typeof(int)), ("data_source_type", typeof(int)),
            ("http_endpoint_json", typeof(string)), ("plugin_name", typeof(string)), ("description", typeof(string)),
            ("long_description", typeof(string)), ("doc_source", typeof(string)), ("required_filter_columns_json", typeof(string)),
            ("texts_json", typeof(string)), ("min_group_size", typeof(int)), ("non_personal", typeof(int)), ("dbt_unique_id", typeof(string)),
            ("object_kind", typeof(string)), ("metric_view_count_measure", typeof(string))
        ];
        foreach (var (name, type) in columns) table.Columns.Add(name, type);

        var row = table.NewRow();
        row["id"] = Guid.NewGuid().ToString();
        row["source_type"] = "Databricks";
        row["source_name"] = "dbx";
        row["schema_name"] = "analytics";
        row["table_name"] = "sales_mv";
        row["display_name"] = "sales_mv";
        row["sensitivity"] = "NORMAL";
        row["requires_four_eyes"] = 0;
        row["is_active"] = 1;
        row["data_source_type"] = 0;
        row["object_kind"] = "metric_view";
        row["metric_view_count_measure"] = "row_count";
        table.Rows.Add(row);

        using var reader = table.CreateDataReader();
        reader.Read().ShouldBeTrue();

        var entity = CatalogRowHydrator.ReadTableEntity(reader);
        entity.ObjectKind.ShouldBe(DiscoveryObjectKind.MetricView);
        entity.MetricViewCountMeasure.ShouldBe("row_count");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln"))) dir = dir.Parent;
        dir.ShouldNotBeNull("Autheris.sln was not found above test output directory.");
        return dir.FullName;
    }
}
