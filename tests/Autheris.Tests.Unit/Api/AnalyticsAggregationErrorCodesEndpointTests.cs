using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Analytics;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace Autheris.Tests.Unit.Api;

/// <summary>Issue #389: distinct governed REST codes for aggregation refusals; fixed messages, no echo in body or logs.</summary>
public sealed class AnalyticsAggregationErrorCodesEndpointTests
{
    private const string Sentinel = "SENTINEL-secret-column-alias";
    private const string PrivacyMessage = "The requested aggregate is not available under the privacy policy.";
    private const string NotSupportedMessage = "The requested aggregation is not supported for this dataset.";

    private sealed class CapturingProvider : ILoggerProvider, ILogger
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? string.Empty : "|EXC|" + exception));
    }

    private static (DefaultHttpContext Ctx, CapturingProvider Log) Context()
    {
        var log = new CapturingProvider();
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u")], "TestAuth")) };
        ctx.RequestServices = new ServiceCollection().AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(log)).BuildServiceProvider();
        ctx.Response.Body = new MemoryStream();
        return (ctx, log);
    }

    private static async Task<(int Status, string Body)> RunAsync(IResult result, DefaultHttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
    }

    private static readonly AnalyticsEndpoints.AggregateRequestDto Request =
        new("sales.dbo.orders", Aggregates: [new AnalyticsEndpoints.AggregateSelectionDto("count", null, "n")]);

    private static async Task<(int Status, string Body, CapturingProvider Log)> AggregateAsync(Exception failure)
    {
        var service = Substitute.For<IGovernedAggregationService>();
        service.ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<AggregationQuery>(), Arg.Any<CancellationToken>())
            .Returns<AggregationResult>(_ => throw failure);
        var (ctx, log) = Context();
        var r = await RunAsync(await AnalyticsEndpoints.HandleAggregateAsync(Request, ctx, service, default), ctx);
        return (r.Status, r.Body, log);
    }

    private static async Task<(int Status, string Body, CapturingProvider Log)> MetricsAsync(Exception failure)
    {
        var service = Substitute.For<ISemanticMetricQueryService>();
        service.QueryMetricsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<MetricQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns<MetricQueryResult>(_ => throw failure);
        var (ctx, log) = Context();
        var r = await RunAsync(await AnalyticsEndpoints.HandleQueryMetricsAsync(new AnalyticsEndpoints.MetricQueryRequestDto(["revenue"]), ctx, service, default), ctx);
        return (r.Status, r.Body, log);
    }

    public static TheoryData<Exception, int, string, string> Causes() => new()
    {
        { new AggregationPrivacyRestrictedException(), 400, "AGGREGATION_PRIVACY_RESTRICTED", PrivacyMessage },
        { new AggregationFilterRejectedException(), 400, "AGG_NEGATED_FILTER_REJECTED", AggregationFilterRejectedException.FixedMessage },
        { new AstBuildException(Sentinel, new InvalidOperationException(Sentinel)), 422, "AGGREGATION_NOT_SUPPORTED", NotSupportedMessage },
        { new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Dialect, Sentinel), 422, "AGGREGATION_NOT_SUPPORTED", NotSupportedMessage },
        { new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.StatementClass, Sentinel), 422, "AGGREGATION_NOT_SUPPORTED", NotSupportedMessage },
        { new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Construct, Sentinel), 422, "AGGREGATION_NOT_SUPPORTED", NotSupportedMessage },
        { new SqlTimeZoneException(GovernedSqlErrorCodes.FunctionUnsupportedDialect), 422, "AGGREGATION_NOT_SUPPORTED", NotSupportedMessage }
    };

    [Theory]
    [MemberData(nameof(Causes))]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I389-T-06")]
    public async Task Rest_Aggregate_PerCause_CodeStatusAndFixedMessage_NoEcho(Exception failure, int status, string code, string message)
    {
        var r = await AggregateAsync(failure);

        r.Status.ShouldBe(status);
        r.Body.ShouldContain(code);
        r.Body.ShouldContain(message);
        r.Body.ShouldNotContain(Sentinel);
        r.Body.ShouldNotContain("SQL_FUNCTION");
        r.Log.Lines.ShouldAllBe(l => !l.Contains(Sentinel));
    }

    [Theory]
    [MemberData(nameof(Causes))]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I389-T-06")]
    public async Task Rest_Metrics_PerCause_CodeStatusAndFixedMessage_NoEcho(Exception failure, int status, string code, string message)
    {
        var r = await MetricsAsync(failure);

        r.Status.ShouldBe(status);
        r.Body.ShouldContain(code);
        r.Body.ShouldContain(message);
        r.Body.ShouldNotContain(Sentinel);
        r.Log.Lines.ShouldAllBe(l => !l.Contains(Sentinel));
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I389-T-08")]
    public async Task RefusalLogs_ContainNoColumnAliasOrInnerMessage()
    {
        foreach (var failure in new Exception[]
        {
            new AstBuildException(Sentinel, new InvalidOperationException(Sentinel)),
            new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Construct, Sentinel),
            new AggregationPrivacyRestrictedException()
        })
        {
            var r = await AggregateAsync(failure);

            r.Log.Lines.ShouldNotBeEmpty();
            r.Log.Lines.ShouldAllBe(l => !l.Contains(Sentinel) && !l.Contains("LATERAL"));
            r.Log.Lines.ShouldContain(l => l.Contains("AGGREGATION_"));
        }
    }

    [Theory]
    [InlineData(GovernedSqlErrorCodes.TimeZoneInvalid)]
    [InlineData(GovernedSqlErrorCodes.TimeZoneArgument)]
    [InlineData(GovernedSqlErrorCodes.TimeZoneUnavailable)]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I389-T-07")]
    public async Task Rest_TimeZoneCodes_NotCaughtByNotSupportedFilter(string code)
    {
        var service = Substitute.For<IGovernedAggregationService>();
        service.ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<AggregationQuery>(), Arg.Any<CancellationToken>())
            .Returns<AggregationResult>(_ => throw new SqlTimeZoneException(code));
        var (ctx, _) = Context();

        await Should.ThrowAsync<SqlTimeZoneException>(async () => await AnalyticsEndpoints.HandleAggregateAsync(Request, ctx, service, default));
    }
}
