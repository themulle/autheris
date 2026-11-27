using Autheris.Application.Analytics;
using Autheris.GraphQL.Catalog;
using Autheris.Domain.Options;
using HotChocolate.Language;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.GraphQL.FlatAggregateTestSupport;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>The pure selection reader of the flat field: group set, distinct aggregates and the synthetic argument nodes.</summary>
public sealed class FlatAggregateSelectionReaderTests
{
    private static ParsedAggregateArguments Parse(
        CatalogTableType table,
        IReadOnlyList<(string ResponseKey, string FieldName, IValueNode? Bucket, IValueNode? TimeZone)> items,
        string? orderBy = null)
    {
        var selection = FlatAggregateSelectionReader.Read(items, table, NoVariables);
        var order = orderBy is null ? null : Utf8GraphQLParser.Syntax.ParseValueLiteral(orderBy, false);
        var (groupBy, aggregates, rewritten) = FlatAggregateSelectionReader.ToArgumentNodes(selection, table, order, NoVariables);
        return AggregateArgumentParser.Parse(table, groupBy, aggregates, rewritten, null, new AnalyticsOptions().Aggregation, NoVariables);
    }

    private static string Code(Action act) => CodeOf(Should.Throw<HotChocolate.GraphQLException>(act))!;

    [Fact]
    public void GroupColumnsAndAggregates_MapToArgumentNodes()
    {
        var table = SalesTable();

        var parsed = Parse(table, Items(("r", "region"), ("total", "amount_sum"), ("n", "count"), ("q", "qty")));

        parsed.GroupBy.Select(c => c.FieldName).ShouldBe(["region", "qty"]);
        parsed.Aggregates.Select(a => (a.Function, a.Column?.FieldName)).ShouldBe(
            [(AggregateFunction.Sum, "amount"), (AggregateFunction.Count, null)]);
        parsed.Aggregates.Select(a => a.Alias).ShouldBe(["agg_0", "agg_1"]);
        parsed.TimeBuckets.ShouldBeNull();
    }

    [Fact]
    public void SameAggregateUnderTwoKeys_ComputedOnce()
    {
        var table = SalesTable();
        var items = Items(("a", "amount_sum"), ("b", "amount_sum"), ("c", "amount_avg"));

        var selection = FlatAggregateSelectionReader.Read(items, table, NoVariables);
        var parsed = Parse(table, items);

        selection.Aggregates.Count.ShouldBe(2);
        selection.AliasByResponseKey["a"].ShouldBe(selection.AliasByResponseKey["b"]);
        selection.AliasByResponseKey["c"].ShouldNotBe(selection.AliasByResponseKey["a"]);
        parsed.Aggregates.Count.ShouldBe(2);
    }

    [Fact]
    public void ZeroAggregates_ArgumentInvalid() =>
        Code(() => Parse(SalesTable(), Items(("r", "region")))).ShouldBe(AggregationErrorCodes.ArgumentInvalid);

    [Fact]
    public void BucketedColumn_BecomesInlineGroupByObject()
    {
        var table = SalesTable();
        IReadOnlyList<(string, string, IValueNode?, IValueNode?)> items =
        [
            ("day", "created", new EnumValueNode("DAY"), new StringValueNode("UTC")),
            ("n", "count", null, null)
        ];

        var selection = FlatAggregateSelectionReader.Read(items, table, NoVariables);
        var (groupBy, _, _) = FlatAggregateSelectionReader.ToArgumentNodes(selection, table, null, NoVariables);
        var parsed = Parse(table, items);

        var spec = ((ListValueNode)groupBy).Items.ShouldHaveSingleItem().ShouldBeOfType<ObjectValueNode>();
        spec.Fields.Select(f => f.Name.Value).ShouldBe(["column", "bucket", "timeZone"]);
        parsed.TimeBuckets.ShouldNotBeNull().ShouldHaveSingleItem().Column.ShouldBe("created");
    }

    [Fact]
    public void SameColumnDifferentBuckets_ArgumentInvalid()
    {
        IReadOnlyList<(string, string, IValueNode?, IValueNode?)> items =
        [
            ("a", "created", new EnumValueNode("DAY"), null),
            ("b", "created", new EnumValueNode("MONTH"), null),
            ("n", "count", null, null)
        ];

        Code(() => Parse(SalesTable(), items)).ShouldBe(AggregationErrorCodes.ArgumentInvalid);
    }

    [Fact]
    public void TimeZoneWithoutBucket_ArgumentInvalid()
    {
        IReadOnlyList<(string, string, IValueNode?, IValueNode?)> items =
        [
            ("a", "created", null, new StringValueNode("UTC")),
            ("n", "count", null, null)
        ];

        Code(() => Parse(SalesTable(), items)).ShouldBe(AggregationErrorCodes.ArgumentInvalid);
    }

    [Fact]
    public void OrderByResponseKey_RewrittenToSyntheticAlias()
    {
        var table = SalesTable();

        var parsed = Parse(table, Items(("r", "region"), ("n", "count"), ("total", "amount_sum")),
            """[{ alias: "total", direction: DESC }, { column: region }]""");

        parsed.OrderBy.ShouldBe([new TableOrderBy("agg_1", true), new TableOrderBy("region", false)]);
    }

    [Fact]
    public void OrderByUnknownOrColumnKey_OrderInvalid()
    {
        var table = SalesTable();
        var items = Items(("r", "region"), ("n", "count"));

        Code(() => Parse(table, items, """[{ alias: "missing" }]""")).ShouldBe(AggregationErrorCodes.OrderInvalid);
        Code(() => Parse(table, items, """[{ alias: "r" }]""")).ShouldBe(AggregationErrorCodes.OrderInvalid);
        Code(() => Parse(table, items, """[{ alias: "agg_0" }]""")).ShouldBe(AggregationErrorCodes.OrderInvalid);
        Code(() => Parse(table, Items(("n", "count")), """[{ column: region }]""")).ShouldBe(AggregationErrorCodes.OrderInvalid);
    }

    [Fact]
    public void SyntheticAlias_AvoidsColumnNames()
    {
        var table = Table(
            ("agg_0", CatalogFieldType.Int),
            ("AGG_1", CatalogFieldType.Int),
            ("region", CatalogFieldType.String));

        var selection = FlatAggregateSelectionReader.Read(Items(("a", "count"), ("b", "agg_0_sum")), table, NoVariables);

        selection.Aggregates.Select(a => a.Alias).ShouldBe(["agg_0_", "agg_1_"]);
        selection.Aggregates.Select(a => a.Alias).ShouldAllBe(a => a != "count" && !a.StartsWith("__", StringComparison.Ordinal));
    }
}
