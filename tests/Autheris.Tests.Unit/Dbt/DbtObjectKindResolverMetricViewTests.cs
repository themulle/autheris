using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Dbt;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Dbt;

public sealed class DbtObjectKindResolverMetricViewTests
{
    private static GatewayOptions DatabricksOptions()
    {
        var opts = new GatewayOptions { Dbt = new DbtOptions { ActivateNewTables = true } };
        opts.DataSources.Connections["dbx"] = new() { Provider = "Databricks", ConnectionString = "Host=example" };
        return opts;
    }

    private static DbtModelDefinition MakeSource(
        string materialization,
        IReadOnlyDictionary<string, string> tableMeta,
        IReadOnlyDictionary<string, DbtColumnDefinition> columns) =>
        new(
            UniqueId: "source.proj.dbx.sales_mv",
            Name: "sales_mv",
            Database: "dbx",
            Schema: "analytics",
            Materialization: materialization,
            Description: null,
            Tags: [],
            Meta: tableMeta,
            Columns: columns,
            DependsOnNodes: []);

    [Fact]
    public void Source_MetricView_IsCataloged()
    {
        var warnings = new List<string>();
        var cols = new Dictionary<string, DbtColumnDefinition>
        {
            ["region"] = new("region", "string", null, [], new Dictionary<string, string> { ["autheris.column_role"] = "dimension" }),
            ["revenue"] = new("revenue", "decimal(18,2)", null, [], new Dictionary<string, string> { ["autheris.column_role"] = "measure" })
        };
        var model = MakeSource("source", new Dictionary<string, string> { ["autheris.object_kind"] = "metric_view" }, cols);

        DbtCatalogTableFactory.TryBuild(model, DatabricksOptions(), out var metadata, out var reason, applyObjectKind: true, warnings: warnings).ShouldBeTrue();
        reason.ShouldBeNull();
        metadata!.Table.ObjectKind.ShouldBe(DiscoveryObjectKind.MetricView);
        metadata.Columns.Single(c => c.ColumnName == "region").Role.ShouldBe(MetricViewColumnRole.Dimension);
        metadata.Columns.Single(c => c.ColumnName == "revenue").Role.ShouldBe(MetricViewColumnRole.Measure);
        warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Model_MetricView_IsRejected()
    {
        var warnings = new List<string>();
        var cols = new Dictionary<string, DbtColumnDefinition>
        {
            ["region"] = new("region", "string", null, [], new Dictionary<string, string> { ["autheris.column_role"] = "dimension" })
        };
        var model = MakeSource("table", new Dictionary<string, string> { ["autheris.object_kind"] = "metric_view" }, cols);

        DbtCatalogTableFactory.TryBuild(model, DatabricksOptions(), out var metadata, out var reason, applyObjectKind: true, warnings: warnings).ShouldBeFalse();
        metadata.ShouldBeNull();
        reason.ShouldNotBeNullOrWhiteSpace();
        warnings.ShouldNotBeEmpty();
    }

    [Fact]
    public void MetricView_ColumnWithoutRole_NotExposed()
    {
        var warnings = new List<string>();
        var cols = new Dictionary<string, DbtColumnDefinition>
        {
            ["region"] = new("region", "string", null, [], new Dictionary<string, string> { ["autheris.column_role"] = "dimension" }),
            ["secret_col"] = new("secret_col", "string", null, [], new Dictionary<string, string>())
        };
        var model = MakeSource("source", new Dictionary<string, string> { ["autheris.object_kind"] = "metric_view" }, cols);

        DbtCatalogTableFactory.TryBuild(model, DatabricksOptions(), out var metadata, out _, applyObjectKind: true, warnings: warnings).ShouldBeTrue();
        var undeclared = metadata!.Columns.Single(c => c.ColumnName == "secret_col");
        undeclared.Role.ShouldBeNull();
        undeclared.DiscoveryHidden.ShouldBeTrue();
    }

    [Fact]
    public void NonMetricView_ColumnRole_IsRejected()
    {
        var warnings = new List<string>();
        var cols = new Dictionary<string, DbtColumnDefinition>
        {
            ["region"] = new("region", "string", null, [], new Dictionary<string, string> { ["autheris.column_role"] = "dimension" })
        };
        var model = MakeSource("source", new Dictionary<string, string> { ["autheris.object_kind"] = "table" }, cols);

        DbtCatalogTableFactory.TryBuild(model, DatabricksOptions(), out var metadata, out var reason, applyObjectKind: true, warnings: warnings).ShouldBeFalse();
        metadata.ShouldBeNull();
        reason.ShouldNotBeNullOrWhiteSpace();
        warnings.ShouldNotBeEmpty();
    }
}
