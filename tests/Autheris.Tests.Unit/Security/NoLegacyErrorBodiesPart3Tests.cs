namespace Autheris.Tests.Unit.Security;

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// NL-SC-20/23 (O-5b step 2, WP-5/6): reachable handlers and middleware answer an application/problem+json body with the expected status
/// and governed code, no top-level legacy <c>error</c> member and no echo of the request input.
/// </summary>
[Trait("Category", "Security")]
public sealed class NoLegacyErrorBodiesPart3Tests
{
    private const string Canary = "CANARY-9c1e-do-not-echo";

    private static DefaultHttpContext Authenticated()
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "S-1-5-21-77"), new Claim("tenant_id", "home")], "Bearer"))
        };
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.1.2.3");
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<(int Status, string ContentType, JsonElement Body, string Raw)> Read(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        var raw = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var json = JsonDocument.Parse(raw).RootElement.Clone();
        return (ctx.Response.StatusCode, ctx.Response.ContentType ?? string.Empty, json, raw);
    }

    private static async Task<(int Status, string ContentType, JsonElement Body, string Raw)> Execute(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        return await Read(ctx);
    }

    private static void ShouldBeGovernedProblem((int Status, string ContentType, JsonElement Body, string Raw) r, int status, string code)
    {
        r.Status.ShouldBe(status);
        r.ContentType.ShouldStartWith("application/problem+json");
        r.Body.GetProperty("code").GetString().ShouldBe(code);
        r.Body.TryGetProperty("error", out _).ShouldBeFalse("no legacy top-level error member");
        r.Raw.ShouldNotContain(Canary);
    }

    [Fact]
    public async Task TokenRevocation_Revoked_Is_401_Unauthorized_Problem()
    {
        var service = Substitute.For<ITokenRevocationService>();
        service.IsRevokedAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(true));
        var ctx = Authenticated();

        await new TokenRevocationMiddleware(_ => Task.CompletedTask).InvokeAsync(ctx, service);

        ShouldBeGovernedProblem(await Read(ctx), StatusCodes.Status401Unauthorized, "TOKEN_REVOKED");
    }

    [Fact]
    public async Task TokenRevocation_Unavailable_Fails_Closed_With_503_And_Retry_After()
    {
        var service = Substitute.For<ITokenRevocationService>();
        service.IsRevokedAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<bool>>(_ => throw new TokenRevocationUnavailableException(Canary));
        var ctx = Authenticated();

        await new TokenRevocationMiddleware(_ => Task.CompletedTask).InvokeAsync(ctx, service);

        ShouldBeGovernedProblem(await Read(ctx), StatusCodes.Status503ServiceUnavailable, "TOKEN_REVOCATION_UNAVAILABLE");
        ctx.Response.Headers.RetryAfter.ToString().ShouldBe("5");
    }

    [Fact]
    public async Task SubjectLockout_Locked_Subject_Is_401_Problem_And_Store_Failure_Is_503()
    {
        var store = Substitute.For<IIdentityStoreRepository>();
        store.IsSubjectLockedAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var locked = Authenticated();
        await new SubjectLockoutMiddleware(_ => Task.CompletedTask, NullLogger<SubjectLockoutMiddleware>.Instance).InvokeAsync(locked, store);
        ShouldBeGovernedProblem(await Read(locked), StatusCodes.Status401Unauthorized, "UNAUTHORIZED");

        store.IsSubjectLockedAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException(Canary));
        var failing = Authenticated();
        await new SubjectLockoutMiddleware(_ => Task.CompletedTask, NullLogger<SubjectLockoutMiddleware>.Instance).InvokeAsync(failing, store);
        ShouldBeGovernedProblem(await Read(failing), StatusCodes.Status503ServiceUnavailable, "SERVICE_UNAVAILABLE");
        failing.Response.Headers.RetryAfter.ToString().ShouldBe("5");
    }

    [Fact]
    public async Task PreAuthIpRateLimit_Is_429_Problem_With_Consistent_Retry_After()
    {
        var limiter = Substitute.For<IRateLimiterService>();
        limiter.CheckPreAuthIpAsync(Arg.Any<string>(), Arg.Any<PreAuthIpRateLimitOptions>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult(new RateLimitResult(false, 7)));
        var options = Options.Create(new GatewayOptions());
        var ctx = Authenticated();
        ctx.Request.Path = "/api/data";

        await new PreAuthIpRateLimitingMiddleware(_ => Task.CompletedTask, options, limiter).InvokeAsync(ctx);

        var r = await Read(ctx);
        ShouldBeGovernedProblem(r, StatusCodes.Status429TooManyRequests, "RATE_LIMIT_EXCEEDED");
        ctx.Response.Headers.RetryAfter.ToString().ShouldBe("7");
        r.Body.GetProperty("retryAfterSeconds").GetInt32().ShouldBe(7);
    }

    [Fact]
    public async Task BodyLimit_Is_413_Problem_Without_Echoing_The_Supplied_Message_Control_Characters()
    {
        var result = await EndpointSecurity.WithBodyLimitAsync(
            () => throw new Microsoft.AspNetCore.Http.BadHttpRequestException("x", StatusCodes.Status413PayloadTooLarge),
            "Body too large.\r\n" + Canary.Replace("CANARY", "X"));

        var r = await Execute(result);
        r.Status.ShouldBe(StatusCodes.Status413PayloadTooLarge);
        r.ContentType.ShouldStartWith("application/problem+json");
        r.Body.GetProperty("code").GetString().ShouldBe("PAYLOAD_TOO_LARGE");
        r.Body.GetProperty("detail").GetString()!.ShouldNotContain("\n");
        r.Body.TryGetProperty("error", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("not valid!", StatusCodes.Status400BadRequest, "INVALID_TENANT_ID")]
    public async Task TenantRegistryAdmin_Invalid_Id_Is_A_Governed_Problem_Without_Echo(string tenantId, int status, string code)
    {
        var registry = Substitute.For<ITenantRegistry>();
        var ctx = Authenticated();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "S-1-5-21-ADMIN1"), new Claim("tenant_id", "home"), new Claim(ClaimTypes.Role, "ClusterAdmin")], "Bearer"));

        var result = await TenantRegistryAdminEndpoints.ResolveCollisionAsync(
            tenantId + Canary, null, ctx, registry, global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(Substitute.For<IAuditLogRepository>()), default);

        ShouldBeGovernedProblem(await Execute(result), status, code);
    }
}
