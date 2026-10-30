namespace Autheris.Tests.Unit.Api;

using System.Text.Json;
using Autheris.Api.Middleware;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

/// <summary>NL-SC-20 / NL-T-30: the WebSQL denial mapper and the global exception handler share one code path and write the same problem shape.</summary>
[Trait("Category", "Security")]
public sealed class GatewayDenialProblemShapeTests
{
    public static TheoryData<GatewaySecurityException, int, string> Denials => new()
    {
        { new GatewayUnauthorizedException(), 401, "UNAUTHORIZED" },
        { new GatewaySecurityException("secret detail", "RESPONSE_TOO_LARGE"), 400, "BAD_REQUEST" },
        { RowFilterKeySetErrorCodes.Unavailable(), 503, "SERVICE_UNAVAILABLE" },
        { new GatewaySecurityException("secret detail table x"), 403, "ACCESS_DENIED" }
    };

    [Theory]
    [MemberData(nameof(Denials))]
    public async Task Gateway_Denial_Mapper_And_Global_Handler_Produce_Same_Problem_Shape(GatewaySecurityException denial, int status, string code)
    {
        var viaMapper = NewContext();
        await GatewayDenialStatusMapper.WriteWebSqlResponseAsync(viaMapper, denial, NullLogger.Instance, default);

        var viaHandler = NewContext();
        (await new GatewayExceptionHandler(NullLogger<GatewayExceptionHandler>.Instance).TryHandleAsync(viaHandler, denial, default)).ShouldBeTrue();

        viaMapper.Response.StatusCode.ShouldBe(status);
        viaHandler.Response.StatusCode.ShouldBe(status);
        viaMapper.Response.ContentType.ShouldStartWith("application/problem+json");
        viaHandler.Response.ContentType.ShouldStartWith("application/problem+json");

        var mapperBody = Read(viaMapper);
        var handlerBody = Read(viaHandler);
        mapperBody.ShouldBe(handlerBody);

        using var doc = JsonDocument.Parse(mapperBody);
        doc.RootElement.GetProperty("code").GetString().ShouldBe(code);
        doc.RootElement.GetProperty("status").GetInt32().ShouldBe(status);
        doc.RootElement.TryGetProperty("error", out _).ShouldBeFalse();
        mapperBody.ShouldNotContain("secret detail");
    }

    private static DefaultHttpContext NewContext()
    {
        var ctx = new DefaultHttpContext { TraceIdentifier = "trace-fixed" };
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string Read(DefaultHttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return new StreamReader(ctx.Response.Body).ReadToEnd();
    }
}
