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

    [Fact]
    public async Task GatewayNotImplementedException_Returns501_WithGenericTitle_WithoutInternalDetails()
    {
        // S-2: No internal configuration or datasource details leaked to client
        var (status, contentType, body) = await HandleAsync(
            new GatewayNotImplementedException("No active database connection configured for data source 'secret_ds_internal'. Synthetic fallback is disabled."));

        status.ShouldBe(StatusCodes.Status501NotImplemented);
        contentType.ShouldStartWith("application/problem+json");
        body.ShouldNotContain("secret_ds_internal");
        body.ShouldNotContain("Synthetic fallback");
        body.ShouldContain("The requested feature or data source capability is not implemented.");
    }

    [Fact]
    public async Task GatewayUnsupportedColumnTypeException_Returns501_WithGenericTitle_WithoutInternalDetails()
    {
        // S-2: No raw column name or internal geometry type leaked
        var (status, contentType, body) = await HandleAsync(
            new GatewayUnsupportedColumnTypeException("secret_col", "geometry"));

        status.ShouldBe(StatusCodes.Status501NotImplemented);
        contentType.ShouldStartWith("application/problem+json");
        body.ShouldNotContain("secret_col");
        body.ShouldContain("The requested column type is not supported.");
    }

    [Fact]
    public async Task ParseCanceledException_Returns400_WithGenericTitle()
    {
        // S-2: SQL syntax / parsing errors must return 400 Bad Request instead of 500
        var (status, contentType, body) = await HandleAsync(
            new Antlr4.Runtime.Misc.ParseCanceledException("line 1:15 no viable alternative at input 'SELECT * FROM WHERE'"));

        status.ShouldBe(StatusCodes.Status400BadRequest);
        contentType.ShouldStartWith("application/problem+json");
        body.ShouldNotContain("no viable alternative");
        body.ShouldContain("Invalid SQL syntax.");
    }

    private sealed class StartedResponseFeature : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}
