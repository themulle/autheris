using System.Text;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using System.Security.Claims;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>WP-PF-08 / SEC-SC-10: cluster-shared Basic marker (keyed HMAC bound to a credential version) and the IP limiter after authentication.</summary>
[Trait("Category", "Security")]
public sealed class BasicAuthSharedMarkerTests
{
    private const string Header = "Basic YWxpY2U6c2VjcmV0";
    private static readonly byte[] SecretA = Encoding.UTF8.GetBytes("cluster-marker-secret-A-0123456789");
    private static readonly byte[] SecretB = Encoding.UTF8.GetBytes("cluster-marker-secret-B-0123456789");

    private static IDistributedCache NewCache() => new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private static BasicAuthAttemptGuard Replica(IDistributedCache cache, byte[]? secret, string? markerKeyRef = "kv://basic-marker", int successSeconds = 30)
    {
        var options = new BasicAuthOptions { SuccessCacheSeconds = successSeconds, MarkerKeyRef = markerKeyRef };
        var guard = new BasicAuthAttemptGuard(options, null, cache, null);
        guard.ConfigureSharedMarker(_ => secret);
        return guard;
    }

    [Fact]
    public async Task SEC_SC_10_MarkerWrittenByOneReplica_IsHonouredByAnother()
    {
        var cache = NewCache();
        var a = Replica(cache, SecretA);
        var b = Replica(cache, SecretA);

        await a.CacheSharedSuccessAsync(Header, "v1", "alice");

        (await b.TryGetSharedSuccessAsync(Header, "v1")).ShouldBe("alice");
    }

    [Fact]
    public async Task SEC_SC_10_Marker_IsBoundToTheCredentialVersion()
    {
        var cache = NewCache();
        var a = Replica(cache, SecretA);
        await a.CacheSharedSuccessAsync(Header, BasicAuthAttemptGuard.CredentialVersion("$argon2id$old"), "alice");

        // The password was rotated: the new stored hash yields another version and the old marker is dead.
        (await a.TryGetSharedSuccessAsync(Header, BasicAuthAttemptGuard.CredentialVersion("$argon2id$new"))).ShouldBeNull();
    }

    [Fact]
    public void SEC_SC_10_CredentialVersion_ChangesWithTheStoredHash_AndRevealsNothing()
    {
        var v1 = BasicAuthAttemptGuard.CredentialVersion("$argon2id$one");
        BasicAuthAttemptGuard.CredentialVersion("$argon2id$one").ShouldBe(v1);
        BasicAuthAttemptGuard.CredentialVersion("$argon2id$two").ShouldNotBe(v1);
        v1.ShouldNotContain("argon2id");
    }

    [Fact]
    public async Task SEC_SC_10_Marker_IsAKeyedHmac_NotDerivedFromThePlaintextAlone()
    {
        var keyA = BasicAuthAttemptGuard.BuildMarkerKey(SecretA, Header, "v1");
        var keyB = BasicAuthAttemptGuard.BuildMarkerKey(SecretB, Header, "v1");

        keyA.ShouldNotBe(keyB);
        keyA.ShouldNotContain("YWxpY2U6c2VjcmV0");

        // A replica holding another secret (or none) can neither read nor forge the marker.
        var cache = NewCache();
        await Replica(cache, SecretA).CacheSharedSuccessAsync(Header, "v1", "alice");
        (await Replica(cache, SecretB).TryGetSharedSuccessAsync(Header, "v1")).ShouldBeNull();
    }

    [Fact]
    public async Task SEC_SC_10_WithoutMarkerKey_TheSharedMarkerIsDisabled_AndNothingIsStored()
    {
        var cache = Substitute.For<IDistributedCache>();
        var noRef = Replica(cache, SecretA, markerKeyRef: null);
        var emptySecret = Replica(cache, [], markerKeyRef: "kv://x");
        var failingResolver = new BasicAuthAttemptGuard(new BasicAuthOptions { MarkerKeyRef = "kv://x" }, null, cache, null);
        failingResolver.ConfigureSharedMarker(_ => throw new InvalidOperationException("vault down"));

        foreach (var g in new[] { noRef, emptySecret, failingResolver })
        {
            await g.CacheSharedSuccessAsync(Header, "v1", "alice");
            (await g.TryGetSharedSuccessAsync(Header, "v1")).ShouldBeNull();
        }

        await cache.DidNotReceiveWithAnyArgs().SetAsync(default!, default!, default!, default);
        await cache.DidNotReceiveWithAnyArgs().GetAsync(default!, default);
    }

    [Fact]
    public async Task SEC_SC_10_CacheFailure_IsAMiss_NeverASuccess()
    {
        var cache = Substitute.For<IDistributedCache>();
        cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("redis down"));
        cache.SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("redis down"));
        var guard = Replica(cache, SecretA);

        await guard.CacheSharedSuccessAsync(Header, "v1", "alice"); // must not throw
        (await guard.TryGetSharedSuccessAsync(Header, "v1")).ShouldBeNull();
    }

    [Fact]
    public async Task SEC_SC_10_Marker_ExpiresWithTheConfiguredSuccessLifetime_AndZeroDisablesIt()
    {
        var cache = Substitute.For<IDistributedCache>();
        var guard = Replica(cache, SecretA, successSeconds: 45);

        await guard.CacheSharedSuccessAsync(Header, "v1", "alice");

        await cache.Received(1).SetAsync(
            Arg.Any<string>(), Arg.Any<byte[]>(),
            Arg.Is<DistributedCacheEntryOptions>(o => o.AbsoluteExpirationRelativeToNow == TimeSpan.FromSeconds(45)),
            Arg.Any<CancellationToken>());

        cache.ClearReceivedCalls();
        var disabled = Replica(cache, SecretA, successSeconds: 0);
        await disabled.CacheSharedSuccessAsync(Header, "v1", "alice");
        (await disabled.TryGetSharedSuccessAsync(Header, "v1")).ShouldBeNull();
        await cache.DidNotReceiveWithAnyArgs().SetAsync(default!, default!, default!, default);
    }

    // ---- IP limiter after authentication -------------------------------------------------------------------------------------

    private static async Task<(bool NextCalled, IRateLimiterService Limiter, DefaultHttpContext Context)> RunPreAuthAsync(
        string authorization, bool basicEnabled, bool exemptAuthenticated = true)
    {
        var limiter = Substitute.For<IRateLimiterService>();
        limiter.CheckPreAuthIpAsync(Arg.Any<string>(), Arg.Any<PreAuthIpRateLimitOptions>(), Arg.Any<CancellationToken>())
            .Returns(new RateLimitResult(true, 0));
        var options = new GatewayOptions
        {
            Authentication = new AuthenticationOptions { BasicAuth = new BasicAuthOptions { Enabled = basicEnabled } },
            RateLimiting = new RateLimitingOptions { PreAuthIpRateLimit = new PreAuthIpRateLimitOptions { ExemptAuthenticatedRequests = exemptAuthenticated } }
        };
        var nextCalled = false;
        var middleware = new PreAuthIpRateLimitingMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, Options.Create(options), limiter);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/datasets";
        context.Request.Headers["Authorization"] = authorization;

        await middleware.InvokeAsync(context);
        return (nextCalled, limiter, context);
    }

    [Fact]
    public async Task SEC_SC_10_BasicRequest_IsExemptFromThePreAuthIpBudget_AndMarkedForPostAuthCharging()
    {
        var (nextCalled, limiter, context) = await RunPreAuthAsync(Header, basicEnabled: true);

        nextCalled.ShouldBeTrue();
        await limiter.DidNotReceiveWithAnyArgs().CheckPreAuthIpAsync(default!, default!, default);
        context.Items.ContainsKey("Autheris_PreAuthExempted").ShouldBeTrue();
    }

    [Fact]
    public async Task SEC_SC_10_BasicRequest_IsStillLimited_WhenBasicIsDisabledOrExemptionIsOff()
    {
        var (_, limiter1, _) = await RunPreAuthAsync(Header, basicEnabled: false);
        await limiter1.ReceivedWithAnyArgs(1).CheckPreAuthIpAsync(default!, default!, default);

        var (_, limiter2, _) = await RunPreAuthAsync(Header, basicEnabled: true, exemptAuthenticated: false);
        await limiter2.ReceivedWithAnyArgs(1).CheckPreAuthIpAsync(default!, default!, default);
    }

    [Fact]
    public async Task SEC_SC_10_UnauthenticatedBasicRequest_IsChargedAgainstTheIpBudget_AfterAuthentication()
    {
        var limiter = Substitute.For<IRateLimiterService>();
        limiter.CheckPreAuthIpAsync(Arg.Any<string>(), Arg.Any<PreAuthIpRateLimitOptions>(), Arg.Any<CancellationToken>())
            .Returns(new RateLimitResult(false, 30));
        var nextCalled = false;
        var mw = new UnauthenticatedBearerIpRateLimitingMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, Options.Create(new GatewayOptions()), limiter);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/datasets";
        context.Request.Headers["Authorization"] = Header;
        context.Response.Body = new MemoryStream();
        context.Items["Autheris_PreAuthExempted"] = true;

        await mw.InvokeAsync(context);

        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);

        var authenticated = new DefaultHttpContext();
        authenticated.Items["Autheris_PreAuthExempted"] = true;
        authenticated.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice")], "Basic"));
        limiter.ClearReceivedCalls();
        await mw.InvokeAsync(authenticated);
        await limiter.DidNotReceiveWithAnyArgs().CheckPreAuthIpAsync(default!, default!, default);
    }
}
