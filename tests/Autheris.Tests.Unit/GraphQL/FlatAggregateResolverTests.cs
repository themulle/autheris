using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Analytics;
using HotChocolate.Execution;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.GraphQL.FlatAggregateTestSupport;
using static Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>The flat <c>&lt;table&gt;_groups</c> field end to end through HotChocolate with a substituted governed service.</summary>
[Collection("CatalogRelation")]
public sealed class FlatAggregateResolverTests
{
    private static IReadOnlyList<(string, string?, string?)> Shape(AggregationQuery? q) =>
        q!.Aggregates!.Select(a => (a.Function.ToString(), a.Column, a.Alias)).ToList();

    [Fact]
    public async Task IssueExample_ReturnsFlatRows_WithAliases()
    {
        var host = new TypedAggregateTestHost();
        var result = Result(
            [Row(new Dictionary<string, object?> { ["region"] = "EU" }, 4, new Dictionary<string, object?> { ["agg_0"] = 12.5m, ["agg_1"] = 4L })],
            total: 4);

        var run = await RunAsync(host, $$"""
            { {{Root}}(limit: 5, orderBy: [{ alias: "total", direction: DESC }]) { region total: amount_sum n: count } }
            """, result);

        Errors(run.Result).ShouldBeEmpty();
        run.Captured!.GroupBy.ShouldBe(["region"]);
        run.Captured.Limit.ShouldBe(5);
        run.Captured.OrderBy.ShouldBe([new TableOrderBy("agg_0", true)]);
        using var doc = Json(run.Result);
        var row = doc.RootElement.GetProperty("data").GetProperty(Root).EnumerateArray().ShouldHaveSingleItem();
        row.GetProperty("region").GetString().ShouldBe("EU");
        row.GetProperty("total").GetDecimal().ShouldBe(12.5m);
        row.GetProperty("n").GetInt64().ShouldBe(4);
    }

    [Fact]
    public async Task CapturedQuery_EqualsNestedQuery_ForSameSelection_TransportGraphql()
    {
        var flat = await RunAsync(new TypedAggregateTestHost(), $$"""
            { {{Root}}(where: { qty: { gt: 3 } }, limit: 7, orderBy: [{ column: region }]) { region s: amount_sum n: count } }
            """);
        var nested = await RunAsync(new TypedAggregateTestHost(), $$"""
            { {{NestedRoot}}(groupBy: [region], aggregates: [{ fn: SUM, column: amount, alias: "agg_0" }, { fn: COUNT, alias: "agg_1" }],
                where: { qty: { gt: 3 } }, limit: 7, orderBy: [{ column: region }]) { count } }
            """);

        Errors(flat.Result).ShouldBeEmpty();
        Errors(nested.Result).ShouldBeEmpty();
        var (f, n) = (flat.Captured!, nested.Captured!);
        f.Transport.ShouldBe("graphql");
        f.Table.ShouldBe(n.Table);
        f.GroupBy.ShouldBe(n.GroupBy);
        Shape(f).ShouldBe(Shape(n));
        f.OrderBy.ShouldBe(n.OrderBy);
        f.Limit.ShouldBe(n.Limit);
        f.Transport.ShouldBe(n.Transport);
        f.Where!.SqlPredicate.ShouldBe(n.Where!.SqlPredicate);
        f.Where.Parameters.Values.ShouldBe(n.Where.Parameters.Values);
    }

    [Fact]
    public async Task Fragments_And_SkipInclude_DetermineGroupSet()
    {
        const string query = """
            query($skip: Boolean!, $inc: Boolean!) {
              sales_dbo_orders_groups {
                region
                ...F
                ... on sales_dbo_orders_group_row { n: count }
                big @skip(if: true)
                ratio @include(if: false)
                id @skip(if: $skip)
                created(bucket: DAY) @include(if: $inc)
                amount_sum @skip(if: $skip)
              }
            }
            fragment F on sales_dbo_orders_group_row { qty }
            """;

        var hidden = await RunAsync(new TypedAggregateTestHost(), query, variables: new() { ["skip"] = true, ["inc"] = false });
        var shown = await RunAsync(new TypedAggregateTestHost(), query, variables: new() { ["skip"] = false, ["inc"] = true });

        Errors(hidden.Result).ShouldBeEmpty();
        hidden.Captured!.GroupBy.ShouldBe(["region", "qty"]);
        Shape(hidden.Captured).ShouldBe([("Count", null, "agg_0")]);
        Errors(shown.Result).ShouldBeEmpty();
        shown.Captured!.GroupBy.ShouldBe(["region", "qty", "id", "created"]);
        Shape(shown.Captured).ShouldBe([("Count", null, "agg_0"), ("Sum", "amount", "agg_1")]);
    }

    [Fact]
    public async Task MergedOtherRow_NullsKeysAndNonMergeableAggregates()
    {
        var host = new TypedAggregateTestHost();
        var result = Result(
            [Row(new Dictionary<string, object?> { ["region"] = "leak" }, 9,
                new Dictionary<string, object?> { ["agg_0"] = 3L, ["agg_1"] = 9L, ["agg_2"] = 20m, ["agg_3"] = 8m }, merged: true)],
            total: 9);

        var run = await RunAsync(host, $$"""
            { {{Root}} { region _isMergedOther d: region_count_distinct n: count s: amount_sum mx: amount_max } }
            """, result);

        Errors(run.Result).ShouldBeEmpty();
        using var doc = Json(run.Result);
        var row = doc.RootElement.GetProperty("data").GetProperty(Root)[0];
        row.GetProperty("_isMergedOther").GetBoolean().ShouldBeTrue();
        row.GetProperty("region").ValueKind.ShouldBe(JsonValueKind.Null);
        row.GetProperty("d").ValueKind.ShouldBe(JsonValueKind.Null);
        row.GetProperty("mx").ValueKind.ShouldBe(JsonValueKind.Null);
        row.GetProperty("n").GetInt64().ShouldBe(9);
        row.GetProperty("s").GetDecimal().ShouldBe(20m);
    }

    [Fact]
    public async Task Extensions_CarryFactsPerResponseKey_TwoRootFields()
    {
        var host = new TypedAggregateTestHost();
        var meta = new AggregationMetadata(5, SmallGroupStrategy.Suppress, true, 2, 7, true, true);

        var run = await RunAsync(host, $$"""
            { zeta: {{Root}} { count } alpha: {{Root}} { region count } }
            """, Result([], total: 11, meta));

        Errors(run.Result).ShouldBeEmpty();
        using var doc = Json(run.Result);
        var facts = Facts(doc);
        facts.EnumerateObject().Select(p => p.Name).ShouldBe(["alpha", "zeta"]);
        foreach (var key in new[] { "alpha", "zeta" })
        {
            var f = facts.GetProperty(key);
            f.EnumerateObject().Select(p => p.Name).ShouldBe(
                ["count", "suppressed", "suppressedGroupsCount", "minimumGroupSize", "minimumGroupSizeSource", "mergedIntoOther", "truncated"]);
            f.GetProperty("count").GetInt64().ShouldBe(11);
            f.GetProperty("suppressed").GetBoolean().ShouldBeTrue();
            f.GetProperty("suppressedGroupsCount").GetInt32().ShouldBe(2);
            f.GetProperty("minimumGroupSize").GetInt32().ShouldBe(5);
            f.GetProperty("minimumGroupSizeSource").GetString().ShouldBe("default");
            f.GetProperty("mergedIntoOther").GetBoolean().ShouldBeTrue();
            f.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Unauthenticated_Unauthorized_NoServiceCall()
    {
        var host = new TypedAggregateTestHost { User = new ClaimsPrincipal(new ClaimsIdentity()) };

        var run = await RunAsync(host, $"{{ {Root} {{ count }} }}");

        Errors(run.Result).Select(e => e.Code).ShouldBe(["UNAUTHORIZED"]);
        run.Queries.ShouldBeEmpty();
    }

    [Fact]
    public async Task MaskedColumnException_MapsToAggregationOnMaskedColumn()
    {
        var host = new TypedAggregateTestHost();
        host.Aggregation.ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<AggregationQuery>(), Arg.Any<CancellationToken>())
            .Returns<AggregationResult>(_ => throw new MaskedAggregationException("secret_column"));
        var executor = await host.BuildAsync();

        var response = await executor.ExecuteAsync($"{{ {Root} {{ region count }} }}");

        var errors = Errors(response);
        errors.Select(e => e.Code).ShouldBe(["AGGREGATION_ON_MASKED_COLUMN"]);
        errors.ShouldAllBe(e => !e.Message.Contains("secret_column"));
    }

    [Fact]
    public async Task TimeBucketLimit_SameCodeAsNested()
    {
        var flat = await RunAsync(new TypedAggregateTestHost(), $"{{ {Root}(limit: 5000) {{ created(bucket: DAY) count }} }}");
        var nested = await RunAsync(new TypedAggregateTestHost(), $$"""
            { {{NestedRoot}}(timeBuckets: [{ column: created, bucket: DAY }], aggregates: [{ fn: COUNT, alias: "n" }], limit: 5000) { count } }
            """);

        Errors(flat.Result).Select(e => e.Code).ShouldBe(["AGGREGATION_TIME_BUCKET_LIMIT_EXCEEDED"]);
        Errors(nested.Result).Select(e => e.Code).ShouldBe(Errors(flat.Result).Select(e => e.Code));
        flat.Queries.ShouldBeEmpty();
    }
}
