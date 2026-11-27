using Autheris.Application.Analytics;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.GraphQL.DatasetFactsTestHost;

namespace Autheris.Tests.Unit.GraphQL;

[Collection("CatalogRelation")]
public sealed class DatasetFactsAggregateTests
{
    private const string Query = "{ agg: sales_dbo_orders_aggregate(aggregates: [{ fn: COUNT, alias: \"n\" }]) { count suppressedGroupsCount minimumGroupSize mergedIntoOther } }";

    private static AggregationResult Result(int suppressedGroups, bool exposed, int minGroup = 5, bool merged = false, bool truncated = false, string[]? filters = null) =>
        new(Orders, [], new AggregationGroupRow(new Dictionary<string, object?>(), 3, new Dictionary<string, object?>()),
            new AggregationMetadata(minGroup, SmallGroupStrategy.Suppress, suppressedGroups > 0, suppressedGroups, 0, merged, truncated))
        {
            AccessFacts = new DatasetAccessFacts(Orders, [], filters ?? []),
            SuppressedCountsExposed = exposed
        };

    private static async Task<(DatasetFactsTestHost Host, object? Unused, HotChocolate.Execution.IRequestExecutor Executor)> BuildAsync(Func<AggregationQuery, AggregationResult> produce)
    {
        var host = new DatasetFactsTestHost { AggregationEnabled = true, Aggregate = produce };
        host.VisibleTables = [Table(Customers), Table(Orders, ["finance"])];
        return (host, null, await host.BuildAsync());
    }

    [Fact]
    public async Task SuppressedGroups_ReportedWithoutSelectingFields()
    {
        var (_, _, executor) = await BuildAsync(_ => Result(2, exposed: true, merged: true, truncated: true, filters: [FixedDescription]));

        var roots = Roots(await RunAsync(executor, "{ agg: sales_dbo_orders_aggregate(aggregates: [{ fn: COUNT, alias: \"n\" }]) { count } }"))!.Value;

        var agg = roots.GetProperty("agg");
        agg.GetProperty("dataset").GetString().ShouldBe("sales.dbo.orders");
        agg.GetProperty("suppressedGroupsCount").GetInt32().ShouldBe(2);
        agg.GetProperty("minimumGroupSize").GetInt32().ShouldBe(5);
        agg.GetProperty("mergedIntoOther").GetBoolean().ShouldBeTrue();
        agg.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        agg.GetProperty("rowFilterApplied").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task AggregateEntry_HasNoRelationsKey()
    {
        var (_, _, executor) = await BuildAsync(_ => Result(0, exposed: false));

        var agg = Roots(await RunAsync(executor, Query))!.Value.GetProperty("agg");

        agg.TryGetProperty("relations", out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SuppressedGroupsCount_OmittedWhenExposeOptionOff()
    {
        var (_, _, executor) = await BuildAsync(_ => Result(0, exposed: false));

        var agg = Roots(await RunAsync(executor, Query))!.Value.GetProperty("agg");

        agg.TryGetProperty("suppressedGroupsCount", out _).ShouldBeFalse();
        agg.GetProperty("mergedIntoOther").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SuppressedGroupsCount_ReportedWhenOptionOn_EqualsSelectedField()
    {
        var (_, _, executor) = await BuildAsync(_ => Result(4, exposed: true));

        var result = await RunAsync(executor, Query);

        var selected = Json(result).RootElement.GetProperty("data").GetProperty("agg").GetProperty("suppressedGroupsCount").GetInt32();
        Roots(result)!.Value.GetProperty("agg").GetProperty("suppressedGroupsCount").GetInt32().ShouldBe(selected);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task MinimumGroupSizeAndMerged_EqualSelectableFields()
    {
        var (_, _, executor) = await BuildAsync(_ => Result(1, exposed: true, minGroup: 7, merged: true));

        var result = await RunAsync(executor, Query);

        var data = Json(result).RootElement.GetProperty("data").GetProperty("agg");
        var agg = Roots(result)!.Value.GetProperty("agg");
        agg.GetProperty("minimumGroupSize").GetInt32().ShouldBe(data.GetProperty("minimumGroupSize").GetInt32());
        agg.GetProperty("mergedIntoOther").GetBoolean().ShouldBe(data.GetProperty("mergedIntoOther").GetBoolean());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DeniedAggregate_NoEntry()
    {
        var host = new DatasetFactsTestHost { AggregationEnabled = true };
        host.VisibleTables = [Table(Customers), Table(Orders)];
        var executor = await host.BuildAsync();

        var result = await RunAsync(executor, Query);

        (Roots(result)?.TryGetProperty("agg", out _) ?? false).ShouldBeFalse();
    }
}
