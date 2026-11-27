using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Analytics;
using Autheris.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Api;

/// <summary>An unknown metric and a metric the caller may not read are indistinguishable on the REST endpoint.</summary>
[Trait("Category", "Security")]
public sealed class MetricQueryNameEnumerationTests
{
    private sealed class ThrowingService(Exception failure) : ISemanticMetricQueryService
    {
        public Task<MetricQueryResult> QueryMetricsAsync(ClaimsPrincipal principal, MetricQueryRequest request, CancellationToken ct = default) =>
            Task.FromException<MetricQueryResult>(failure);
    }

    private static async Task<(int Status, string Body)> RunAsync(Exception failure)
    {
        var service = new ThrowingService(failure);
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u")], "TestAuth")) };
        ctx.RequestServices = new ServiceCollection().AddLogging(b => b.SetMinimumLevel(LogLevel.Trace)).BuildServiceProvider();
        ctx.Response.Body = new MemoryStream();
        var result = await AnalyticsEndpoints.HandleQueryMetricsAsync(new AnalyticsEndpoints.MetricQueryRequestDto(["revenue"]), ctx, service, default);
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        // The per-request correlation and trace ids legitimately differ; everything else must match.
        return (ctx.Response.StatusCode, Regex.Replace(body, "\"(correlationId|traceId)\":\"[^\"]*\"", "\"$1\":\"-\""));
    }

    [Fact]
    public async Task Unknown_And_Denied_Metric_Responses_Are_Identical()
    {
        var unknown = await RunAsync(new KeyNotFoundException("Metric 'x' was not found"));
        var denied = await RunAsync(new GatewayForbiddenException("Access denied."));
        var missingTable = await RunAsync(new TableNotFoundException(new Autheris.Domain.Common.TableIdentifier("sales", "public", "orders")));

        unknown.Status.ShouldBe(404);
        unknown.Body.ShouldContain("METRIC_NOT_FOUND");
        denied.ShouldBe(unknown);
        missingTable.ShouldBe(unknown);
    }
}
