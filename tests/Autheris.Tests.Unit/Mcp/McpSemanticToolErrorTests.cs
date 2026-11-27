namespace Autheris.Tests.Unit.Mcp;

using System.Security.Claims;
using Autheris.Application.Analytics;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Shouldly;
using Xunit;

/// <summary>query_metrics: an unknown, an unresolvable and a denied metric are one answer; other failures keep a specific code and never the exception text.</summary>
public sealed class McpSemanticToolErrorTests
{
    private sealed class Failing(Exception failure) : ISemanticMetricQueryService
    {
        public Task<MetricQueryResult> QueryMetricsAsync(ClaimsPrincipal principal, MetricQueryRequest request, CancellationToken ct = default) => throw failure;
    }

    private static Task<string> Call(Exception failure, string args = """{"metrics":["revenue"]}""") =>
        McpSemanticTools.ExecuteQueryMetricsAsync(new Failing(failure), new ClaimsPrincipal(new ClaimsIdentity("test")), args);

    [Fact]
    [Trait("Category", "Security")]
    public async Task QueryMetrics_UnknownAndDeniedMetric_AreByteIdentical_MetricNotFound()
    {
        var results = new[]
        {
            await Call(new KeyNotFoundException("metric revenue is unknown")),
            await Call(new GatewayForbiddenException("no consent on sales.public.orders", "FORBIDDEN")),
            await Call(new TableNotFoundException(new TableIdentifier("sales", "public", "orders")))
        };

        McpErrorEnvelopeAssert.Code(results[0]).ShouldBe("METRIC_NOT_FOUND");
        McpErrorEnvelopeAssert.Message(results[0]).ShouldBe("One or more requested metrics were not found in the semantic catalog.");
        results.ShouldAllBe(r => r == results[0]);
        results[0].ShouldNotContain("orders");
        results[0].ShouldNotContain("revenue");
    }

    [Fact]
    public async Task QueryMetrics_KeyNotFound_MetricNotFound()
    {
        McpErrorEnvelopeAssert.Code(await Call(new KeyNotFoundException())).ShouldBe("METRIC_NOT_FOUND");
    }

    [Fact]
    public async Task QueryMetrics_ArgumentException_InvalidMetricQuery()
    {
        var json = await Call(new ArgumentException("dimension 'ssn' is not allowed"));

        McpErrorEnvelopeAssert.Code(json).ShouldBe("INVALID_METRIC_QUERY");
        json.ShouldNotContain("ssn");
    }

    [Fact]
    public async Task QueryMetrics_ColumnLevelAggregationCode_IsKept()
    {
        var json = await Call(new GatewaySecurityException("masked", "AGGREGATION_ON_MASKED_COLUMN"));

        McpErrorEnvelopeAssert.Code(json).ShouldBe("AGGREGATION_ON_MASKED_COLUMN");
        json.ShouldNotContain("masked\"");
    }

    [Fact]
    public async Task QueryMetrics_BadWhere_NotInternalError()
    {
        // A where clause the service rejects with an argument error is answered inside the tool, never as an internal error.
        var json = await Call(new ArgumentException("bad predicate ((("), """{"metrics":["revenue"],"where":"((("}""");

        McpErrorEnvelopeAssert.Code(json).ShouldNotBe("INTERNAL_ERROR");
    }
}
