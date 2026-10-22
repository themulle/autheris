namespace Autheris.Tests.Unit.Api;

using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class RetryAfterHeaderTests
{
    [Fact]
    public async Task GatewayExceptionHandler_Sets429_RetryAfterHeader_AndJsonBody()
    {
        var logger = NullLogger<GatewayExceptionHandler>.Instance;
        var handler = new GatewayExceptionHandler(logger);

        var context = new DefaultHttpContext();
        var bodyStream = new MemoryStream();
        context.Response.Body = bodyStream;

        var throttledException = new GatewayThrottledException("Rate limit reached", retryAfterSeconds: 12);

        var handled = await handler.TryHandleAsync(context, throttledException, CancellationToken.None);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        context.Response.Headers.RetryAfter.ToString().ShouldBe("12");

        bodyStream.Position = 0;
        using var jsonDoc = await JsonDocument.ParseAsync(bodyStream);
        var root = jsonDoc.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(429);
        root.GetProperty("code").GetString().ShouldBe("TOO_MANY_REQUESTS");
        root.GetProperty("retryAfterSeconds").GetInt32().ShouldBe(12);
    }

    [Fact]
    public async Task GatewayExceptionHandler_MapsGovernanceStoreNotReady_To503WithRetryAfter_API06()
    {
        var handler = new GatewayExceptionHandler(NullLogger<GatewayExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(
            context, new Autheris.Infrastructure.Persistence.Migrations.GovernanceStoreNotReadyException("governance"), CancellationToken.None);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers.RetryAfter.ToString().ShouldNotBeNullOrEmpty();
    }
}
