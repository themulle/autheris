using Autheris.Application.Analytics;
using Autheris.Application.Dbt.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>#112 A-8: typed metric result mapping (T-4.4, T-4.5, SI-112-6, SEC-112-20).</summary>
public sealed class MetricResultMapperTests
{
    private static readonly TableIdentifier Id = new("sales", "dbo", "orders");

    private static MetricQueryResult Result(
        IReadOnlyList<AggregationGroupRow> groups,
        AggregationMetadata? meta = null,
        IReadOnlyDictionary<string, AggregateValueKind>? metricKinds = null,
        IReadOnlyDictionary<string, AggregateValueKind>? dimensionKinds = null) =>
        new(
            ["revenue", "orders"],
            "v7",
            groups,
            new AggregationGroupRow(new Dictionary<string, object?>(), 40, new Dictionary<string, object?>()),
            meta ?? new AggregationMetadata(5, SmallGroupStrategy.Suppress, false, 0, 0, false, false),
            new Dictionary<string, MetricDefinitionSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["revenue"] = new("revenue", "simple", "Revenue", "Total revenue", "EUR"),
                ["orders"] = new("orders", "simple", null, null, null)
            })
        {
            MetricValueKinds = metricKinds ?? new Dictionary<string, AggregateValueKind>
            {
                ["revenue"] = AggregateValueKind.Decimal, ["orders"] = AggregateValueKind.Int
            },
            DimensionValueKinds = dimensionKinds ?? new Dictionary<string, AggregateValueKind>(StringComparer.OrdinalIgnoreCase)
            {
                ["region"] = AggregateValueKind.String, ["day"] = AggregateValueKind.DateTime, ["vip"] = AggregateValueKind.Boolean, ["tier"] = AggregateValueKind.Int
            }
        };

    [Fact]
    public void Map_TypesDimensionsAndValues_InRequestOrder()
    {
        var day = new DateTime(2026, 5, 6, 0, 0, 0, DateTimeKind.Utc);
        var group = new AggregationGroupRow(
            new Dictionary<string, object?> { ["region"] = "EU", ["day"] = day, ["vip"] = true, ["tier"] = 2 }, 9,
            new Dictionary<string, object?> { ["orders"] = 9L, ["revenue"] = 120.5m });

        var dto = MetricResultMapper.Map(Result([group]));

        var g = dto.Groups.Single();
        g.Dimensions.Select(d => d.Alias).ShouldBe(["region", "day", "vip", "tier"]);
        g.Dimensions[0].StringValue.ShouldBe("EU");
        g.Dimensions[1].DateTimeValue.ShouldBe(new DateTimeOffset(day));
        g.Dimensions[2].BooleanValue.ShouldBe(true);
        g.Dimensions[3].IntValue.ShouldBe(2L);
        g.Values.Select(v => v.Alias).ShouldBe(["revenue", "orders"]);
        g.Values[0].Kind.ShouldBe(AggregateValueKind.Decimal);
        g.Values[0].DecimalValue.ShouldBe(120.5m);
        g.Values[1].IntValue.ShouldBe(9L);
        g.RowCount.ShouldBe(9L);
        dto.Count.ShouldBe(40L);
        dto.ManifestVersion.ShouldBe("v7");
        dto.Metrics.ShouldBe(["revenue", "orders"]);
    }

    [Fact]
    public void Map_NullValue_KeepsTheStaticKind()
    {
        var group = new AggregationGroupRow(new Dictionary<string, object?>(), 1, new Dictionary<string, object?> { ["revenue"] = null });

        var value = MetricResultMapper.Map(Result([group])).Groups.Single().Values[0];

        value.Kind.ShouldBe(AggregateValueKind.Decimal);
        value.DecimalValue.ShouldBeNull();
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Map_MergedOtherRow_HasNullDimensions()
    {
        var group = new AggregationGroupRow(
            new Dictionary<string, object?> { ["region"] = "leak" }, 9, new Dictionary<string, object?> { ["revenue"] = 1m, ["orders"] = 2L }, IsMergedOther: true);

        var g = MetricResultMapper.Map(Result([group])).Groups.Single();

        g.IsMergedOther.ShouldBeTrue();
        g.Dimensions.ShouldAllBe(d => d.StringValue == null && d.IntValue == null && d.BooleanValue == null && d.DateTimeValue == null);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Map_Definitions_AreTheAllowListSummary_SI_112_6()
    {
        var dto = MetricResultMapper.Map(Result([]));

        dto.MetricDefinitions.Select(d => d.Name).ShouldBe(["revenue", "orders"]);
        dto.MetricDefinitions[0].Label.ShouldBe("Revenue");
        dto.MetricDefinitions[0].Unit.ShouldBe("EUR");
        typeof(MetricDefinitionDto).GetProperties().Select(p => p.Name).ShouldBe(["Name", "Type", "Label", "Description", "Unit"], ignoreOrder: true);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Map_HasNoSuppressedRowCount_SEC_112_20()
    {
        var dto = MetricResultMapper.Map(Result([], new AggregationMetadata(5, SmallGroupStrategy.Suppress, true, 2, 99, true, true, true)));

        dto.Suppressed.ShouldBeTrue();
        dto.SuppressedGroupsCount.ShouldBe(2);
        dto.MinimumGroupSize.ShouldBe(5);
        dto.MergedIntoOther.ShouldBeTrue();
        dto.Truncated.ShouldBeTrue();
        dto.NoiseApplied.ShouldBeTrue();
        typeof(MetricResultDto).GetProperties().Select(p => p.Name).ShouldNotContain("SuppressedRowsCount");
    }

    [Fact]
    public void Map_UnconvertibleMetricValue_RaisesResultTypeMismatch()
    {
        var group = new AggregationGroupRow(new Dictionary<string, object?>(), 1, new Dictionary<string, object?> { ["orders"] = "secret" });

        var ex = Should.Throw<HotChocolate.GraphQLException>(() => MetricResultMapper.Map(Result([group])));

        ex.Errors.Single().Code.ShouldBe(AggregationErrorCodes.ResultTypeMismatch);
        ex.Errors.Single().Message.ShouldNotContain("secret");
    }
}
