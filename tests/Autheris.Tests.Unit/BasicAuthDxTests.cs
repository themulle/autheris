using System.Security.Claims;
using System.Text.Json;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>
/// F-AUTH-DX: session/logout endpoints, E-13 rate limiting of /metrics and the shared JSON/CORS helpers.
/// </summary>
public sealed class BasicAuthDxTests
{
    private static ClaimsPrincipal SessionPrincipal(string jti = "abc123")
    {
        var user = new BasicAuthUserConfig { Username = "alice", Password = "dev", TenantId = "tenant-a", Roles = ["Analyst"] };
        return BasicAuthSession.CreateSessionPrincipal(user, jti, DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    private static async Task<JsonElement> ExecuteAsync(IResult result, HttpContext context)
    {
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return (await JsonDocument.ParseAsync(context.Response.Body)).RootElement.Clone();
    }

    private static DefaultHttpContext ContextWith(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configure(services);
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    // ---------- /api/auth/session ----------

    [Fact]
    public async Task GetSession_ForSessionPrincipal_ReturnsIdentityAndSessionInfo()
    {
        var context = ContextWith(_ => { });
        context.User = SessionPrincipal("abc123");

        var json = await ExecuteAsync(AuthEndpoints.GetSession(context), context);

        json.GetProperty("user").GetString().ShouldBe("alice");
        json.GetProperty("tenant").GetString().ShouldBe("tenant-a");
        json.GetProperty("authMethod").GetString().ShouldBe(BasicAuthSession.AuthMethodSession);
        json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldContain("Analyst");
        json.GetProperty("session").GetProperty("id").GetString().ShouldBe("abc123");
    }

    [Fact]
    public async Task GetSession_ForHeaderLogin_HasNoSessionBlock()
    {
        var context = ContextWith(_ => { });
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "alice"), new Claim(BasicAuthSession.AuthMethodClaimType, BasicAuthSession.AuthMethodBasic)],
            GatewayAuthSchemes.Basic));

        var json = await ExecuteAsync(AuthEndpoints.GetSession(context), context);

        json.GetProperty("authMethod").GetString().ShouldBe(BasicAuthSession.AuthMethodBasic);
        json.GetProperty("session").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ---------- /api/auth/logout ----------

    private static (DefaultHttpContext Context, IAuthenticationService Auth, ITokenRevocationService Revocation) LogoutContext(
        ClaimsPrincipal user, GatewayOptions? options = null)
    {
        var auth = Substitute.For<IAuthenticationService>();
        var revocation = Substitute.For<ITokenRevocationService>();
        var schemes = Substitute.For<IAuthenticationSchemeProvider>();
        schemes.GetSchemeAsync(BasicAuthSession.SchemeName)
            .Returns(new AuthenticationScheme(BasicAuthSession.SchemeName, null, typeof(IAuthenticationHandler)));

        var context = ContextWith(s =>
        {
            s.AddSingleton(auth);
            s.AddSingleton(revocation);
            s.AddSingleton(schemes);
            s.AddSingleton(Options.Create(options ?? new GatewayOptions()));
        });
        context.User = user;
        return (context, auth, revocation);
    }

    [Fact]
    public async Task Logout_WithSession_RevokesJtiAndDeletesCookie()
    {
        var (context, auth, revocation) = LogoutContext(SessionPrincipal("abc123"));

        var json = await ExecuteAsync(await AuthEndpoints.LogoutAsync(context), context);

        json.GetProperty("loggedOut").GetBoolean().ShouldBeTrue();
        json.GetProperty("sessionRevoked").GetBoolean().ShouldBeTrue();
        await revocation.Received(1).RevokeAsync("abc123", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await auth.Received(1).SignOutAsync(context, BasicAuthSession.SchemeName, Arg.Any<AuthenticationProperties>());
    }

    [Fact]
    public async Task Logout_Anonymous_ClearsCookieWithoutRevocation()
    {
        var (context, auth, revocation) = LogoutContext(new ClaimsPrincipal(new ClaimsIdentity()));

        var json = await ExecuteAsync(await AuthEndpoints.LogoutAsync(context), context);

        json.GetProperty("sessionRevoked").GetBoolean().ShouldBeFalse();
        await revocation.DidNotReceiveWithAnyArgs().RevokeAsync(default!, default, default);
        await auth.Received(1).SignOutAsync(context, BasicAuthSession.SchemeName, Arg.Any<AuthenticationProperties>());
    }

    [Fact]
    public async Task Logout_HeaderLoginWithoutSession_DoesNotRevokeTheUser()
    {
        var basic = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], GatewayAuthSchemes.Basic));
        var (context, _, revocation) = LogoutContext(basic);

        await AuthEndpoints.LogoutAsync(context);

        await revocation.DidNotReceiveWithAnyArgs().RevokeAsync(default!, default, default);
    }

    // ---------- E-13: /metrics and /health in the pre-auth IP limiter ----------

    private static async Task<(bool NextCalled, IRateLimiterService Limiter)> RunPreAuthAsync(string path)
    {
        var limiter = Substitute.For<IRateLimiterService>();
        limiter.CheckPreAuthIpAsync(Arg.Any<string>(), Arg.Any<PreAuthIpRateLimitOptions>(), Arg.Any<CancellationToken>())
            .Returns(new RateLimitResult(true, 0));
        var nextCalled = false;
        var middleware = new PreAuthIpRateLimitingMiddleware(_ => { nextCalled = true; return Task.CompletedTask; },
            Options.Create(new GatewayOptions()), limiter);

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        await middleware.InvokeAsync(context);
        return (nextCalled, limiter);
    }

    [Fact]
    public async Task PreAuthRateLimiter_Metrics_IsRateLimited()
    {
        var (nextCalled, limiter) = await RunPreAuthAsync("/metrics");

        nextCalled.ShouldBeTrue();
        await limiter.Received(1).CheckPreAuthIpAsync(Arg.Any<string>(), Arg.Any<PreAuthIpRateLimitOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreAuthRateLimiter_Health_IsSkipped()
    {
        var (nextCalled, limiter) = await RunPreAuthAsync("/health/ready");

        nextCalled.ShouldBeTrue();
        await limiter.DidNotReceiveWithAnyArgs().CheckPreAuthIpAsync(default!, default!, default);
    }

    // ---------- Shared helpers ----------

    [Fact]
    public void JsonValueText_ConvertsAllValueKinds()
    {
        using var doc = JsonDocument.Parse("""{"s":"x","t":true,"f":false,"n":90,"d":1.50,"z":null,"o":{"k":"v"},"a":[1,2]}""");
        string Get(string name) => JsonValueText.From(doc.RootElement.GetProperty(name));

        Get("s").ShouldBe("x");
        Get("t").ShouldBe("true");
        Get("f").ShouldBe("false");
        Get("n").ShouldBe("90");
        Get("d").ShouldBe("1.50");
        Get("z").ShouldBe(string.Empty);
        Get("o").ShouldBe("""{"k":"v"}""");
        Get("a").ShouldBe("[1,2]");
    }

    [Fact]
    public void IsWildcardCors_CoversWarnFlagQuickstartAndWildcardOrigin()
    {
        new GatewayOptions().IsWildcardCors.ShouldBeFalse();
        new GatewayOptions { GraphQL = new GraphQLOptions { TrustedOrigins = ["https://a.example"] } }.IsWildcardCors.ShouldBeFalse();
        new GatewayOptions { GraphQL = new GraphQLOptions { TrustedOrigins = ["*"] } }.IsWildcardCors.ShouldBeTrue();
        new GatewayOptions { GraphQL = new GraphQLOptions { warn_allow_all_cors_origins = true } }.IsWildcardCors.ShouldBeTrue();
    }
}
