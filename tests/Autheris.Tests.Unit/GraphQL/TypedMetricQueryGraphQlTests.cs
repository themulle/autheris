using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Analytics;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using HotChocolate.Execution;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>#112 A-8: the single typed <c>queryMetrics</c> field (T-4.5, T-5.13, T-5.15, T-S-09, T-S-14).</summary>
public sealed class TypedMetricQueryGraphQlTests
{
    private static MetricQueryResult Sample() =>
        new(["revenue"], "v3",
            [new AggregationGroupRow(new Dictionary<string, object?> { ["region"] = "EU" }, 6, new Dictionary<string, object?> { ["revenue"] = 10.5m })],
            new AggregationGroupRow(new Dictionary<string, object?>(), 6, new Dictionary<string, object?>()),
            new AggregationMetadata(5, SmallGroupStrategy.Suppress, false, 0, 0, false, false),
            new Dictionary<string, MetricDefinitionSummary> { ["revenue"] = new("revenue", "simple", "Revenue", null, "EUR") })
        {
            MetricValueKinds = new Dictionary<string, AggregateValueKind> { ["revenue"] = AggregateValueKind.Decimal },
            DimensionValueKinds = new Dictionary<string, AggregateValueKind> { ["region"] = AggregateValueKind.String }
        };

    private static async Task<(IExecutionResult Result, MetricQueryRequest? Captured)> RunAsync(
        TypedAggregateTestHost host, string query, Dictionary<string, object?>? variables = null, Func<MetricQueryResult>? result = null)
    {
        MetricQueryRequest? captured = null;
        host.Metrics.QueryMetricsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Do<MetricQueryRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult((result ?? Sample)()));
        var executor = await host.BuildAsync();
        var request = OperationRequestBuilder.New().SetDocument(query);
        if (variables != null)
        {
            request.SetVariableValues(variables);
        }

        return (await executor.ExecuteAsync(request.Build()), captured);
    }

    [Fact]
    public async Task HappyPath_WithFilterVariable_ReturnsTypedRows_AndSetsTheTypedWhere()
    {
        var host = new TypedAggregateTestHost();

        var (response, captured) = await RunAsync(host, """
            query($f: AutherisMetricFilter) {
              queryMetrics(metrics: ["revenue"], groupBy: ["region"], where: $f, orderBy: [{ metric: "revenue", direction: DESC }], limit: 10) {
                metrics manifestVersion count suppressed suppressedGroupsCount minimumGroupSize mergedIntoOther truncated noiseApplied
                groups { rowCount isMergedOther dimensions { name kind stringValue } values { alias kind decimalValue } }
                metricDefinitions { name type label description unit }
              }
            }
            """,
            new Dictionary<string, object?>
            {
                ["f"] = new Dictionary<string, object?>
                {
                    ["dimension"] = new Dictionary<string, object?>
                    {
                        ["name"] = "region",
                        ["where"] = new Dictionary<string, object?> { ["string"] = new Dictionary<string, object?> { ["eq"] = "EU" } }
                    }
                }
            });

        Errors(response).ShouldBeEmpty();
        captured!.Where.ShouldBeNull();
        var leaf = captured.TypedWhere.ShouldBeOfType<TreeComparison>();
        leaf.Column.ShouldBe("region");
        leaf.Value.ShouldBe("EU");
        leaf.DeclaredKind.ShouldBe(ColumnValueKind.String);
        captured.OrderBy.ShouldBe([new TableOrderBy("revenue", true)]);
        captured.Limit.ShouldBe(10);

        using var doc = Json(response);
        var data = doc.RootElement.GetProperty("data").GetProperty("queryMetrics");
        data.GetProperty("manifestVersion").GetString().ShouldBe("v3");
        data.GetProperty("count").GetInt64().ShouldBe(6);
        var group = data.GetProperty("groups")[0];
        group.GetProperty("dimensions")[0].GetProperty("kind").GetString().ShouldBe("STRING");
        group.GetProperty("dimensions")[0].GetProperty("stringValue").GetString().ShouldBe("EU");
        group.GetProperty("values")[0].GetProperty("kind").GetString().ShouldBe("DECIMAL");
        group.GetProperty("values")[0].GetProperty("decimalValue").GetDecimal().ShouldBe(10.5m);
        data.GetProperty("metricDefinitions")[0].GetProperty("unit").GetString().ShouldBe("EUR");
    }

    public static TheoryData<Exception, string> Failures() => new()
    {
        { new KeyNotFoundException("Metric 'secret_metric' was not found in the semantic catalog."), "UNKNOWN_METRIC" },
        { new GatewayForbiddenException("Access denied."), "UNKNOWN_METRIC" },
        { new TableNotFoundException(new TableIdentifier("sales", "dbo", "secret_table")), "UNKNOWN_METRIC" },
        { new MaskedAggregationException("secret_column"), "AGGREGATION_ON_MASKED_COLUMN" },
        { new AggregationColumnUnavailableException(), "AGGREGATION_COLUMN_UNAVAILABLE" },
        { new MetricFilterInvalidException(), "METRIC_FILTER_INVALID" },
        { new AggregationRowCapExceededException(), "AGGREGATION_ROW_CAP_EXCEEDED" },
        { new PrivacyBudgetExhaustedException("alice", 1.0, 0.0), "PRIVACY_BUDGET_EXHAUSTED" },
        { new ArgumentException("Dimension 'secret_dim' is not declared"), "AGGREGATION_ARGUMENT_INVALID" },
        { new GatewayUnauthorizedException("secret"), "UNAUTHORIZED" }
    };

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(Failures))]
    public async Task ServiceFailures_MapToFixedCodesAndMessages_NeverEcho(Exception failure, string code)
    {
        var host = new TypedAggregateTestHost();
        host.Metrics.QueryMetricsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<MetricQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns<MetricQueryResult>(_ => throw failure);
        var executor = await host.BuildAsync();

        var response = await executor.ExecuteAsync("{ queryMetrics(metrics: [\"revenue\"]) { count } }");

        var errors = Errors(response);
        errors.Select(e => e.Code).ShouldBe([code]);
        errors.ShouldAllBe(e => !e.Message.Contains("secret") && !e.Message.Contains("sales_dbo"));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task UnknownForeignTenantAndNoConsentMetrics_AreIndistinguishable_T_S_09()
    {
        var outcomes = new List<(string? Code, string Message)>();
        foreach (var failure in new Exception[]
                 {
                     new KeyNotFoundException("unknown"), new KeyNotFoundException("foreign tenant"), new GatewayForbiddenException("Access denied.")
                 })
        {
            var host = new TypedAggregateTestHost();
            host.Metrics.QueryMetricsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<MetricQueryRequest>(), Arg.Any<CancellationToken>())
                .Returns<MetricQueryResult>(_ => throw failure);
            var response = await (await host.BuildAsync()).ExecuteAsync("{ queryMetrics(metrics: [\"revenue\"]) { count } }");
            outcomes.Add(Errors(response).Single());
        }

        outcomes.Distinct().Count().ShouldBe(1);
        outcomes[0].Code.ShouldBe("UNKNOWN_METRIC");
        outcomes[0].Message.ShouldBe("The metric is not available.");
    }

    [Fact]
    public async Task UnauthenticatedCaller_IsRejectedBeforeTheService()
    {
        var host = new TypedAggregateTestHost { User = new ClaimsPrincipal(new ClaimsIdentity()) };

        var (response, captured) = await RunAsync(host, "{ queryMetrics(metrics: [\"revenue\"]) { count } }");

        Errors(response).Select(e => e.Code).ShouldBe(["UNAUTHORIZED"]);
        captured.ShouldBeNull();
    }

    [Fact]
    public async Task MalformedFilter_IsRejectedBeforeTheService_WithAFixedCode()
    {
        var host = new TypedAggregateTestHost();

        var (response, captured) = await RunAsync(host,
            "{ queryMetrics(metrics: [\"revenue\"], where: { dimension: { name: \"region\", where: { int: { eq: 1 } } } }, limit: 0) { count } }");

        Errors(response).Select(e => e.Code).ShouldBe(["AGGREGATION_ARGUMENT_INVALID"]);
        captured.ShouldBeNull();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task OneOfRule_RejectsTwoTypedLeaves_BeforeTheService()
    {
        var host = new TypedAggregateTestHost();

        var (response, captured) = await RunAsync(host,
            "{ queryMetrics(metrics: [\"revenue\"], where: { dimension: { name: \"region\", where: { int: { eq: 1 }, string: { eq: \"x\" } } } }) { count } }");

        Errors(response).ShouldNotBeEmpty();
        captured.ShouldBeNull();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Result_CarriesNoFilterTemplateTypeParamsOrMeta_SI_112_6()
    {
        var host = new TypedAggregateTestHost();

        var (response, _) = await RunAsync(host,
            "{ queryMetrics(metrics: [\"revenue\"]) { metricDefinitions { name type label description unit } } }");

        Errors(response).ShouldBeEmpty();
        using var doc = Json(response);
        doc.RootElement.GetProperty("data").GetProperty("queryMetrics").GetProperty("metricDefinitions")[0]
            .EnumerateObject().Select(p => p.Name).ShouldBe(["name", "type", "label", "description", "unit"]);
    }
}
