using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public sealed class SrM02BearerIpRateLimitTests
{
    private static IRateLimiterService Limiter(bool allowed)
    {
        var limiter = Substitute.For<IRateLimiterService>();
        limiter.CheckPreAuthIpAsync(Arg.Any<string>(), Arg.Any<PreAuthIpRateLimitOptions>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(new RateLimitResult(allowed, 30));
        return limiter;
    }

    private static DefaultHttpContext BearerContext(bool authenticated)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/consents";
        context.Request.Headers["Authorization"] = "Bearer bogus";
        context.Response.Body = new MemoryStream();
        context.Items["Autheris_PreAuthExempted"] = true;
        if (authenticated)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "u1") }, "test"));
        }
        return context;
    }

    [Fact]
    public async Task SR_M_02_UnauthenticatedBearerRequest_IsRateLimited_BeforeAuthorization()
    {
        var nextCalled = false;
        var mw = new UnauthenticatedBearerIpRateLimitingMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            Options.Create(new GatewayOptions()), Limiter(false));
        var context = BearerContext(authenticated: false);

        await mw.InvokeAsync(context);

        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        context.Response.Headers.RetryAfter.ToString().ShouldBe("30");
    }

    [Fact]
    public async Task SR_M_02_UnauthenticatedBearerRequest_WithinBudget_Passes()
    {
        var nextCalled = false;
        var limiter = Limiter(true);
        var mw = new UnauthenticatedBearerIpRateLimitingMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            Options.Create(new GatewayOptions()), limiter);

        await mw.InvokeAsync(BearerContext(authenticated: false));

        nextCalled.ShouldBeTrue();
        await limiter.ReceivedWithAnyArgs(1).CheckPreAuthIpAsync(default!, default!, default);
    }

    [Fact]
    public async Task SR_M_02_AuthenticatedBearerRequest_DoesNotConsumeAnonymousBudget()
    {
        var nextCalled = false;
        var limiter = Limiter(false);
        var mw = new UnauthenticatedBearerIpRateLimitingMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            Options.Create(new GatewayOptions()), limiter);

        await mw.InvokeAsync(BearerContext(authenticated: true));

        nextCalled.ShouldBeTrue();
        await limiter.DidNotReceiveWithAnyArgs().CheckPreAuthIpAsync(default!, default!, default);
    }

    [Fact]
    public async Task SR_M_02_PostAuthMiddleware_NoLongerDoubleCharges_UnauthenticatedRequests()
    {
        var nextCalled = false;
        var limiter = Limiter(false);
        var mw = new PostAuthSidRateLimitingMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            Options.Create(new GatewayOptions()), limiter);

        await mw.InvokeAsync(BearerContext(authenticated: false));

        nextCalled.ShouldBeTrue();
        await limiter.DidNotReceiveWithAnyArgs().CheckPreAuthIpAsync(default!, default!, default);
    }
}
