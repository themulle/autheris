namespace Autheris.Tests.Unit.Dbt;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Policy.Lint;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>#112 SI-112-5, M-2, SEC-112-08/-14/-16: manifest validation before any side effect.</summary>
public sealed class SemanticManifestValidatorTests
{
    private static SemanticManifestValidator Validator(SemanticOptions? options = null) =>
        new(PolicyParserSetup.CreateParser(), Options.Create(options ?? new SemanticOptions()));

    internal static TableMetadata Orders()
    {
        var id = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = new TableIdentifier("analytics", "public", "orders"),
            Table = new Table { Id = id, SourceType = "PostgreSQL", SourceName = "analytics", SchemaName = "public", TableName = "orders", DataSourceType = DataSourceType.Sql, IsActive = true },
            Columns =
            [
                new TableColumn { TableId = id, ColumnName = "id", DataType = "int" },
                new TableColumn { TableId = id, ColumnName = "status", DataType = "varchar" },
                new TableColumn { TableId = id, ColumnName = "amount", DataType = "decimal" }
            ]
        };
    }

    private static DbtSemanticModel Model(string name = "orders", IReadOnlyList<DbtMeasure>? measures = null, IReadOnlyList<DbtDimension>? dims = null) =>
        new(name, "d", "analytics.public.orders", [new DbtEntity("id", "primary")],
            measures ?? [new DbtMeasure("order_total", "sum", "amount", null), new DbtMeasure("order_count", "count", "1", null)],
            dims ?? [new DbtDimension("status", "categorical", null)]);

    private static DbtMetric Metric(string name = "revenue", string? filter = null, int? min = null, IReadOnlyList<string>? measures = null,
        IReadOnlyList<string>? dims = null, IReadOnlyDictionary<string, string>? meta = null, IReadOnlyList<string>? granularities = null,
        string? description = null) =>
        new(name, "simple", description, name, filter, null, dims ?? ["status"], measures ?? ["order_total"], meta ?? new Dictionary<string, string>(),
            MinGroupSize: min, AllowedTimeGranularities: granularities);

    private static SemanticManifestValidationResult Run(IEnumerable<DbtSemanticModel> models, IEnumerable<DbtMetric> metrics, bool withTable = true, SemanticOptions? options = null)
    {
        var list = models.ToList();
        var tables = withTable ? list.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(n => n, _ => Orders(), StringComparer.OrdinalIgnoreCase) : new Dictionary<string, TableMetadata>();
        return Validator(options).Validate(new DbtSemanticManifest(list, metrics.ToList(), "v1"), tables);
    }

    [Fact]
    public void ValidManifest_Passes()
    {
        Run([Model()], [Metric(filter: "status = 'completed'", min: 5)]).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void ValidManifest_WithDbtTemplateSyntax_Passes()
    {
        Run([Model()], [Metric(filter: "{{ Dimension('order_id__status') }} = 'completed'")]).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("duplicateMetric")]
    [InlineData("duplicateModel")]
    [InlineData("danglingMeasure")]
    [InlineData("danglingDimension")]
    [InlineData("badMetricName")]
    [InlineData("emptyMetricName")]
    [InlineData("longMetricName")]
    [InlineData("controlCharDescription")]
    [InlineData("longDescription")]
    [InlineData("minGroupSizeZero")]
    [InlineData("minGroupSizeNegative")]
    [InlineData("minGroupSizeHuge")]
    [InlineData("granularityUnknown")]
    [InlineData("privacyMetaKey")]
    [Trait("Category", "Security")]
    public void T_S_13_InvalidManifest_IsRejected(string scenario)
    {
        var models = new List<DbtSemanticModel> { Model() };
        var metrics = new List<DbtMetric> { Metric() };
        switch (scenario)
        {
            case "duplicateMetric": metrics.Add(Metric("REVENUE")); break;
            case "duplicateModel": models.Add(Model("ORDERS")); break;
            case "danglingMeasure": metrics[0] = Metric(measures: ["nope"]); break;
            case "danglingDimension": metrics[0] = Metric(dims: ["nope"]); break;
            case "badMetricName": metrics[0] = Metric("bad name;"); break;
            case "emptyMetricName": metrics[0] = Metric(""); break;
            case "longMetricName": metrics[0] = Metric(new string('a', 129)); break;
            case "controlCharDescription": metrics[0] = Metric(description: "a\u0001b"); break;
            case "longDescription": metrics[0] = Metric(description: new string('a', 4097)); break;
            case "minGroupSizeZero": metrics[0] = Metric(min: 0); break;
            case "minGroupSizeNegative": metrics[0] = Metric(min: -3); break;
            case "minGroupSizeHuge": metrics[0] = Metric(min: 1_000_001); break;
            case "granularityUnknown": metrics[0] = Metric(granularities: ["fortnight"]); break;
            case "privacyMetaKey": metrics[0] = Metric(meta: new Dictionary<string, string> { ["small_group_strategy"] = "Show" }); break;
        }

        Run(models, metrics).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void ModelCountAbove1000_AndMetricCountAbove5000_AreRejected()
    {
        Run(Enumerable.Range(0, 1001).Select(i => Model($"m{i}")), [Metric()]).IsValid.ShouldBeFalse();
        Run([Model()], Enumerable.Range(0, 5001).Select(i => Metric($"x{i}"))).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("a.b")]
    [InlineData("a\"b")]
    [InlineData("a b")]
    [InlineData("a'b")]
    [InlineData("")]
    [Trait("Category", "Security")]
    public void ManifestImport_ModelNameWithDotOrQuote_Rejected(string modelName)
    {
        Run([Model(modelName)], []).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void ModelNameAbove128Chars_IsRejected() => Run([Model(new string('a', 129))], []).IsValid.ShouldBeFalse();

    [Fact]
    public void MinGroupSizeBoundaries_AreAccepted()
    {
        Run([Model()], [Metric(min: 1)]).IsValid.ShouldBeTrue();
        Run([Model()], [Metric(min: 1_000_000)]).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void MetricTimeDimension_IsAccepted() => Run([Model()], [Metric(dims: ["metric_time", "status"])]).IsValid.ShouldBeTrue();

    [Fact]
    [Trait("Category", "Security")]
    public void FilterTemplate_OverLength_Rejected()
    {
        var filter = "status = '" + new string('a', 200) + "'";
        Run([Model()], [Metric(filter: filter)], options: new SemanticOptions { MaxFilterTemplateLength = 128 }).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("status = 'a'; DROP TABLE orders")]
    [InlineData("status = 'a' -- comment")]
    [InlineData("status = 'a' /* c */")]
    [InlineData("status = 'a")]
    [InlineData("status = \"a")]
    [InlineData("status = 'a'\0")]
    [InlineData("status IN (SELECT status FROM other)")]
    [InlineData("EXISTS (SELECT 1 FROM orders)")]
    [InlineData("pg_sleep(10) IS NOT NULL")]
    [InlineData("status = :unknown")]
    [InlineData("status = :attr_region")]
    [InlineData("secret_col = 'x'")]
    [Trait("Category", "Security")]
    public void FilterTemplate_Unsafe_Rejected(string filter)
    {
        Run([Model()], [Metric(filter: filter)]).IsValid.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void FilterTemplate_Semicolon_Rejected() => Run([Model()], [Metric(filter: "status = 'a';")]).IsValid.ShouldBeFalse();

    [Fact]
    [Trait("Category", "Security")]
    public void FilterTemplate_UnknownPlaceholder_Rejected() => Run([Model()], [Metric(filter: "status = :who")]).IsValid.ShouldBeFalse();

    [Fact]
    [Trait("Category", "Security")]
    public void FilterTemplate_Subquery_Rejected() => Run([Model()], [Metric(filter: "id IN (SELECT id FROM orders)")]).IsValid.ShouldBeFalse();

    [Fact]
    [Trait("Category", "Security")]
    public void FilterTemplate_DisallowedFunction_Rejected() => Run([Model()], [Metric(filter: "xp_cmdshell(status) = 'x'")]).IsValid.ShouldBeFalse();

    [Fact]
    [Trait("Category", "Security")]
    public void SemanticTemplate_CallerAttributeReference_Rejected() => Run([Model()], [Metric(filter: "status = :attr.department")]).IsValid.ShouldBeFalse();

    [Fact]
    [Trait("Category", "Security")]
    public void ManifestImport_FilterTemplateOnUnresolvedTable_Rejected()
    {
        Run([Model()], [Metric(filter: "status = 'x'")], withTable: false).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void ManifestWithoutTemplates_NeedsNoResolvedTable() => Run([Model()], [Metric()], withTable: false).IsValid.ShouldBeTrue();

    [Fact]
    [Trait("Category", "Security")]
    public void Errors_AreFixedCodes_NeverEchoNamesOrTemplates()
    {
        var result = Run([Model()], [Metric("bad name;", filter: "secret_col = 'topsecret'")]);

        result.IsValid.ShouldBeFalse();
        string.Join(' ', result.Errors).ShouldNotContain("secret_col");
        string.Join(' ', result.Errors).ShouldNotContain("topsecret");
        string.Join(' ', result.Errors).ShouldNotContain("bad name");
    }
}
