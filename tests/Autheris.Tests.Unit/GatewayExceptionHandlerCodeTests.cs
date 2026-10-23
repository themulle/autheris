using System.Text.Json;
using Autheris.Api.Middleware;
using Autheris.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>SEC-PG-14 (#313, #295): every ProblemDetails of the global handler carries a code; binding errors are 400 with a fixed message.</summary>
[Trait("Category", "Security")]
public sealed class GatewayExceptionHandlerCodeTests
{
    private static async Task<(int Status, JsonElement Body, string Raw)> HandleAsync(Exception exception)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var handler = new GatewayExceptionHandler(NullLogger<GatewayExceptionHandler>.Instance);
        (await handler.TryHandleAsync(context, exception, CancellationToken.None)).ShouldBeTrue();
        context.Response.Body.Position = 0;
        var raw = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, JsonDocument.Parse(raw).RootElement.Clone(), raw);
    }

    [Fact]
    public async Task BadHttpRequest_Is400BadRequest_WithFixedMessage_SEC_PG_14()
    {
        var (status, body, raw) = await HandleAsync(
            new BadHttpRequestException("Failed to read parameter \"int limit\" from the request query string. Cannot parse 'abc'."));

        status.ShouldBe(400);
        body.GetProperty("code").GetString().ShouldBe("BAD_REQUEST");
        raw.ShouldNotContain("limit");
        raw.ShouldNotContain("abc");
    }

    [Fact]
    public async Task BadHttpRequest413_StaysPayloadTooLarge_SEC_PG_14()
    {
        var (status, body, _) = await HandleAsync(new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));

        status.ShouldBe(413);
        body.GetProperty("code").GetString().ShouldBe("PAYLOAD_TOO_LARGE");
    }

    [Theory]
    [InlineData(StatusCodes.Status500InternalServerError, "INTERNAL_ERROR")]
    [InlineData(StatusCodes.Status501NotImplemented, "NOT_IMPLEMENTED")]
    [InlineData(StatusCodes.Status403Forbidden, "ACCESS_DENIED")]
    [InlineData(StatusCodes.Status400BadRequest, "BAD_REQUEST")]
    public async Task EveryProblem_CarriesACode_SEC_PG_14(int expectedStatus, string expectedCode)
    {
        Exception ex = expectedStatus switch
        {
            500 => new InvalidOperationException("boom"),
            501 => new NotSupportedException("nope"),
            403 => new GatewayForbiddenException("denied"),
            _ => new GatewayInvalidQueryException("bad")
        };

        var (status, body, _) = await HandleAsync(ex);

        status.ShouldBe(expectedStatus);
        body.GetProperty("code").GetString().ShouldBe(expectedCode);
    }
}
