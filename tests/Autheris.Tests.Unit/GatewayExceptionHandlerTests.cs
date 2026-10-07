using System.Data.Common;
using System.Text.Json;
using Autheris.Api.Middleware;
using Autheris.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>
/// O9 (docs/plans/rls-subquery-in-strategy.md, OData-Härtung): unhandled exceptions never reach the developer exception
/// page. Every endpoint answers with application/problem+json, without exception text, stack trace or SQL error.
/// </summary>
public sealed class GatewayExceptionHandlerTests
{
    private sealed class FakeSqlException(int number, string message) : DbException(message)
    {
        public int Number { get; } = number;
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/odata/v4/lwetem_prod/fms/air1";
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "trace-123";
        return context;
    }

    private static async Task<(int Status, string ContentType, string Body)> HandleAsync(Exception exception, Action<DefaultHttpContext>? configure = null)
    {
        var context = CreateContext();
        configure?.Invoke(context);
        var handler = new GatewayExceptionHandler(NullLogger<GatewayExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        handled.ShouldBeTrue();
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, context.Response.ContentType ?? string.Empty, body);
    }

    [Fact]
    public async Task UnexpectedException_Returns500ProblemJson_WithoutDetails()
    {
        var (status, contentType, body) = await HandleAsync(new InvalidOperationException("Fatal unexpected engine crash at /home/runner/work"));

        status.ShouldBe(StatusCodes.Status500InternalServerError);
        contentType.ShouldStartWith("application/problem+json");
        body.ShouldNotContain("Fatal unexpected engine crash");
        body.ShouldNotContain("/home/runner");
        body.ShouldNotContain("<html", Case.Insensitive);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("status").GetInt32().ShouldBe(500);
        doc.RootElement.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SqlTimeout_Returns504_WithoutSqlErrorText()
    {
        var (status, contentType, body) = await HandleAsync(new FakeSqlException(-2, "Execution Timeout Expired. The timeout period elapsed prior to completion of the operation"));

        status.ShouldBe(StatusCodes.Status504GatewayTimeout);
        contentType.ShouldStartWith("application/problem+json");
        body.ShouldNotContain("Execution Timeout Expired");
    }

    [Fact]
    public async Task TransientDatabaseError_Returns503_WithRetryAfter()
    {
        var context = CreateContext();
        var handler = new GatewayExceptionHandler(NullLogger<GatewayExceptionHandler>.Instance);

        await handler.TryHandleAsync(context, new FakeSqlException(1205, "Transaction was deadlocked"), CancellationToken.None);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers.RetryAfter.ToString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Throttled_Returns429_WithRetryAfter()
    {
        var context = CreateContext();
        var handler = new GatewayExceptionHandler(NullLogger<GatewayExceptionHandler>.Instance);

        await handler.TryHandleAsync(context, new GatewayThrottledException(3), CancellationToken.None);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        context.Response.Headers.RetryAfter.ToString().ShouldBe("3");
    }

    [Fact]
    public async Task InvalidQuery_Returns400()
    {
        var (status, _, _) = await HandleAsync(new GatewayInvalidQueryException("The property 'amount' does not exist or is not accessible."));

        status.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ClientAbort_Returns499_WithoutBody()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var (status, _, body) = await HandleAsync(new OperationCanceledException(), c => c.RequestAborted = cts.Token);

        status.ShouldBe(499);
        body.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResponseAlreadyStarted_IsNotHandled()
    {
        var context = CreateContext();
        var feature = new StartedResponseFeature();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(feature);
        var handler = new GatewayExceptionHandler(NullLogger<GatewayExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException("late"), CancellationToken.None);

        handled.ShouldBeFalse();
    }

    private sealed class StartedResponseFeature : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}
