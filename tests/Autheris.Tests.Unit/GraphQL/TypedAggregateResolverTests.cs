using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Analytics;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using HotChocolate.Execution;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>#112 WP-5: the typed <c>T_aggregate</c> field end to end through HotChocolate (T-5.4..T-5.8, T-5.12, T-5.14, T-5.15, T-S-14, T-S-18).</summary>
public sealed class TypedAggregateResolverTests
{
    private const string Field = "sales_dbo_orders_aggregate";

    private static AggregationGroupRow Row(Dictionary<string, object?> key, long count, Dictionary<string, object?> aggs, bool merged = false) =>
        new(key, count, aggs, merged);

    private static async Task<(IExecutionResult Result, AggregationQuery? Captured)> RunAsync(
        TypedAggregateTestHost host, string query, AggregationResult? result = null, Dictionary<string, object?>? variables = null)
    {
        AggregationQuery? captured = null;
        host.Aggregation.ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Do<AggregationQuery>(q => captured = q), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(result ?? Result([])));
        var executor = await host.BuildAsync();
        var request = OperationRequestBuilder.New().SetDocument(query);
        if (variables != null)
        {
            request.SetVariableValues(variables);
        }

        return (await executor.ExecuteAsync(request.Build()), captured);
    }

    [Fact]
    public async Task HappyPath_GroupedSumAndCount_WithWhereVariable_ReturnsTypedGroups()
    {
        var host = new TypedAggregateTestHost();
        var result = Result(
            [Row(new Dictionary<string, object?> { ["region"] = "EU" }, 4, new Dictionary<string, object?> { ["total"] = 12.5m, ["n"] = 4L })],
            total: 4);

        var (response, captured) = await RunAsync(host, """
            query($w: sales_dbo_orders_filter) {
              sales_dbo_orders_aggregate(
                groupBy: [region],
                aggregates: [{ fn: SUM, column: amount, alias: "total" }, { fn: COUNT, alias: "n" }],
                where: $w, orderBy: [{ alias: "total", direction: DESC }], limit: 5) {
                count suppressed suppressedGroupsCount minimumGroupSize mergedIntoOther truncated noiseApplied
                groups { rowCount isMergedOther key { region amount } aggregates { alias kind intValue decimalValue floatValue stringValue dateTimeValue } }
              }
            }
            """, result, new Dictionary<string, object?> { ["w"] = new Dictionary<string, object?> { ["qty"] = new Dictionary<string, object?> { ["gt"] = 3 } } });

        Errors(response).ShouldBeEmpty();
        captured!.Table.ShouldBe(new TableIdentifier("sales", "dbo", "orders"));
        captured.GroupBy.ShouldBe(["region"]);
        captured.Limit.ShouldBe(5);
        captured.Transport.ShouldBe("graphql");
        captured.OrderBy.ShouldBe([new TableOrderBy("total", true)]);
        captured.Aggregates!.Select(a => (a.Function, a.Column, a.Alias)).ShouldBe(
            [(AggregateFunction.Sum, "amount", "total"), (AggregateFunction.Count, null, "n")]);
        captured.Where!.Parameters.Values.ShouldContain(3);
        captured.Where.SqlPredicate.ShouldNotContain("3");

        using var doc = Json(response);
        var data = doc.RootElement.GetProperty("data").GetProperty(Field);
        data.GetProperty("count").GetInt64().ShouldBe(4);
        var group = data.GetProperty("groups")[0];
        group.GetProperty("key").GetProperty("region").GetString().ShouldBe("EU");
        group.GetProperty("key").GetProperty("amount").ValueKind.ShouldBe(JsonValueKind.Null);
        var aggs = group.GetProperty("aggregates");
        aggs[0].GetProperty("alias").GetString().ShouldBe("total");
        aggs[0].GetProperty("kind").GetString().ShouldBe("DECIMAL");
        aggs[0].GetProperty("decimalValue").GetDecimal().ShouldBe(12.5m);
        aggs[0].GetProperty("intValue").ValueKind.ShouldBe(JsonValueKind.Null);
        aggs[1].GetProperty("kind").GetString().ShouldBe("INT");
        aggs[1].GetProperty("intValue").GetInt64().ShouldBe(4);
    }

    [Fact]
    public async Task NullAggregate_KeepsItsKind_AndDateTimeIsUtcIso()
    {
        var host = new TypedAggregateTestHost();
        var when = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var result = Result([Row(new Dictionary<string, object?>(), 3, new Dictionary<string, object?> { ["s"] = null, ["m"] = when })], 3);

        var (response, _) = await RunAsync(host, $$"""
            { {{Field}}(aggregates: [{ fn: SUM, column: amount, alias: "s" }, { fn: MIN, column: created, alias: "m" }]) {
                groups { aggregates { alias kind decimalValue dateTimeValue } } } }
            """, result);

        Errors(response).ShouldBeEmpty();
        using var doc = Json(response);
        var aggs = doc.RootElement.GetProperty("data").GetProperty(Field).GetProperty("groups")[0].GetProperty("aggregates");
        aggs[0].GetProperty("kind").GetString().ShouldBe("DECIMAL");
        aggs[0].GetProperty("decimalValue").ValueKind.ShouldBe(JsonValueKind.Null);
        aggs[1].GetProperty("kind").GetString().ShouldBe("DATE_TIME");
        DateTimeOffset.Parse(aggs[1].GetProperty("dateTimeValue").GetString()!).UtcDateTime.ShouldBe(when);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task MergedOtherRow_HasNullKeys_AndNullCountDistinctAndAvg()
    {
        var host = new TypedAggregateTestHost();
        var result = Result(
            [Row(new Dictionary<string, object?> { ["region"] = "leak", ["qty"] = 4 }, 9,
                new Dictionary<string, object?> { ["d"] = 3L, ["a"] = 2.5m }, merged: true)], 9);

        var (response, _) = await RunAsync(host, $$"""
            { {{Field}}(groupBy: [region, qty], aggregates: [{ fn: COUNT_DISTINCT, column: region, alias: "d" }, { fn: AVG, column: qty, alias: "a" }]) {
                groups { isMergedOther key { region qty } aggregates { alias intValue decimalValue } } } }
            """, result);

        Errors(response).ShouldBeEmpty();
        using var doc = Json(response);
        var group = doc.RootElement.GetProperty("data").GetProperty(Field).GetProperty("groups")[0];
        group.GetProperty("isMergedOther").GetBoolean().ShouldBeTrue();
        group.GetProperty("key").GetProperty("region").ValueKind.ShouldBe(JsonValueKind.Null);
        group.GetProperty("key").GetProperty("qty").ValueKind.ShouldBe(JsonValueKind.Null);
        group.GetProperty("aggregates")[0].GetProperty("intValue").ValueKind.ShouldBe(JsonValueKind.Null);
        group.GetProperty("aggregates")[1].GetProperty("decimalValue").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("[{ fn: AVG, column: region, alias: \"x\" }]", "AGGREGATION_INVALID_FUNCTION_FOR_TYPE")]
    [InlineData("[{ fn: SUM, column: is_vip, alias: \"x\" }]", "AGGREGATION_INVALID_FUNCTION_FOR_TYPE")]
    [InlineData("[{ fn: COUNT, alias: \"bad alias\" }]", "AGGREGATION_ALIAS_INVALID")]
    [InlineData("[{ fn: COUNT, alias: \"count\" }]", "AGGREGATION_ALIAS_INVALID")]
    [InlineData("[]", "AGGREGATION_ARGUMENT_INVALID")]
    public async Task InvalidArguments_AreRejectedBeforeTheEngine_WithFixedCodes(string aggregates, string code)
    {
        var host = new TypedAggregateTestHost();

        var (response, captured) = await RunAsync(host, $"{{ {Field}(aggregates: {aggregates}) {{ count }} }}");

        Errors(response).Select(e => e.Code).ShouldBe([code]);
        captured.ShouldBeNull();
        await host.Aggregation.DidNotReceive().ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<AggregationQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LimitDefaultsToHundred_AndNonPositiveLimitIsRejected()
    {
        var host = new TypedAggregateTestHost();

        var (_, captured) = await RunAsync(host, $"{{ {Field}(aggregates: [{{ fn: COUNT, alias: \"n\" }}]) {{ count }} }}");
        captured!.Limit.ShouldBe(100);

        var (response, _) = await RunAsync(new TypedAggregateTestHost(), $"{{ {Field}(aggregates: [{{ fn: COUNT, alias: \"n\" }}], limit: 0) {{ count }} }}");
        Errors(response).Select(e => e.Code).ShouldBe(["AGGREGATION_ARGUMENT_INVALID"]);
    }

    [Fact]
    public async Task OrderBy_ExactlyOneRule_IsEnforced()
    {
        var host = new TypedAggregateTestHost();

        var (response, _) = await RunAsync(host,
            $"{{ {Field}(groupBy: [region], aggregates: [{{ fn: COUNT, alias: \"n\" }}], orderBy: [{{ column: region, alias: \"n\" }}]) {{ count }} }}");

        Errors(response).Select(e => e.Code).ShouldBe(["AGGREGATION_ORDER_INVALID"]);
    }

    [Fact]
    public async Task ReservedColumnNames_UseTheSuffixedEnumValue()
    {
        var host = new TypedAggregateTestHost();
        var result = Result([Row(new Dictionary<string, object?> { ["null"] = "x" }, 1, new Dictionary<string, object?> { ["n"] = 1L })], 1);

        var (response, captured) = await RunAsync(host,
            $"{{ {Field}(groupBy: [null_col], aggregates: [{{ fn: COUNT, alias: \"n\" }}]) {{ groups {{ key {{ null }} }} }} }}", result);

        Errors(response).ShouldBeEmpty();
        captured!.GroupBy.ShouldBe(["null"]);
        using var doc = Json(response);
        doc.RootElement.GetProperty("data").GetProperty(Field).GetProperty("groups")[0].GetProperty("key").GetProperty("null").GetString().ShouldBe("x");
    }

    [Fact]
    public async Task ResultTypeMismatch_IsReportedWithoutTheValue()
    {
        var host = new TypedAggregateTestHost();
        var result = Result([Row(new Dictionary<string, object?>(), 1, new Dictionary<string, object?> { ["s"] = "secret-text" })], 1);

        var (response, _) = await RunAsync(host,
            $"{{ {Field}(aggregates: [{{ fn: SUM, column: qty, alias: \"s\" }}]) {{ groups {{ aggregates {{ intValue }} }} }} }}", result);

        var errors = Errors(response);
        errors.Select(e => e.Code).ShouldContain("AGGREGATION_RESULT_TYPE_MISMATCH");
        errors.ShouldAllBe(e => !e.Message.Contains("secret"));
    }

    public static TheoryData<Exception, string> EngineFailures() => new()
    {
        { new MaskedAggregationException("secret_column"), "AGGREGATION_ON_MASKED_COLUMN" },
        { new AggregationColumnUnavailableException(), "AGGREGATION_COLUMN_UNAVAILABLE" },
        { new AggregationRowCapExceededException(), "AGGREGATION_ROW_CAP_EXCEEDED" },
        { new GatewayInvalidQueryException("echo secret_value in sales_dbo_orders"), "AGGREGATION_ARGUMENT_INVALID" },
        { new ArgumentException("echo secret_value"), "AGGREGATION_ARGUMENT_INVALID" },
        { new GatewayForbiddenException("Access denied."), "ACCESS_DENIED" },
        { new TableNotFoundException(new TableIdentifier("sales", "dbo", "orders")), "ACCESS_DENIED" },
        { new PrivacyBudgetExhaustedException("alice", 1.0, 0.0), "PRIVACY_BUDGET_EXHAUSTED" },
        { new GatewayUnauthorizedException("secret"), "UNAUTHORIZED" }
    };

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(EngineFailures))]
    public async Task TypedAggregate_Errors_NeverEchoTableColumnOrValue(Exception failure, string code)
    {
        var host = new TypedAggregateTestHost();
        host.Aggregation.ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<AggregationQuery>(), Arg.Any<CancellationToken>())
            .Returns<AggregationResult>(_ => throw failure);
        var executor = await host.BuildAsync();

        var response = await executor.ExecuteAsync($"{{ {Field}(aggregates: [{{ fn: COUNT, alias: \"n\" }}]) {{ count }} }}");

        var errors = Errors(response);
        errors.Select(e => e.Code).ShouldBe([code]);
        errors.ShouldAllBe(e => !e.Message.Contains("secret") && !e.Message.Contains("sales_dbo") && !e.Message.Contains("echo"));
    }

    [Fact]
    public async Task AggregationDisabledByConfiguration_RejectsTheCall()
    {
        var host = new TypedAggregateTestHost();
        host.Analytics.Aggregation.Enabled = false;

        var (response, captured) = await RunAsync(host, $"{{ {Field}(aggregates: [{{ fn: COUNT, alias: \"n\" }}]) {{ count }} }}");

        Errors(response).ShouldHaveSingleItem().Message.ShouldContain("disabled by configuration");
        captured.ShouldBeNull();
    }

    [Fact]
    public async Task UnauthenticatedCaller_IsRejectedBeforeTheEngine()
    {
        var host = new TypedAggregateTestHost { User = new ClaimsPrincipal(new ClaimsIdentity()) };

        var (response, captured) = await RunAsync(host, $"{{ {Field}(aggregates: [{{ fn: COUNT, alias: \"n\" }}]) {{ count }} }}");

        Errors(response).Select(e => e.Code).ShouldBe(["UNAUTHORIZED"]);
        captured.ShouldBeNull();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task TypedWhere_IsBoundAsParameters_NeverAsText()
    {
        var host = new TypedAggregateTestHost();

        var (response, captured) = await RunAsync(host,
            $$"""{ {{Field}}(aggregates: [{ fn: COUNT, alias: "n" }], where: { region: { eq: "x'; DROP TABLE t; --" } }) { count } }""");

        Errors(response).ShouldBeEmpty();
        captured!.Where!.SqlPredicate.ShouldNotContain("DROP");
        captured.Where.Parameters.Values.ShouldContain("x'; DROP TABLE t; --");
        captured.Where.ReferencedColumns.ShouldBe(["region"]);
    }

    [Fact]
    public async Task TypedWhere_CoercionFailure_IsRejectedWithAFixedCode()
    {
        var host = new TypedAggregateTestHost();

        var (response, captured) = await RunAsync(host,
            $$"""{ {{Field}}(aggregates: [{ fn: COUNT, alias: "n" }], where: { created: { gt: "not-a-date-secret" } }) { count } }""");

        var errors = Errors(response);
        errors.Select(e => e.Code).ShouldBe(["AGGREGATION_ARGUMENT_INVALID"]);
        errors.ShouldAllBe(e => !e.Message.Contains("secret"));
        captured.ShouldBeNull();
    }

    [Fact]
    public async Task GraphQlAggregationDisabled_RemovesTheFieldAndTheTypes()
    {
        var host = new TypedAggregateTestHost { AggregationEnabled = false };
        var executor = await host.BuildAsync();
        var sdl = executor.Schema.ToString();

        sdl.ShouldNotContain($"{Field}(");
        sdl.ShouldNotContain("sales_dbo_orders_aggregate_result");
        sdl.ShouldNotContain("sales_dbo_orders_column");
        sdl.ShouldContain("queryMetrics(");
        sdl.ShouldContain("sales_dbo_orders(");
    }
}
