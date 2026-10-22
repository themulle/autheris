using System.Net;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>
/// Security review: ext_authz honours X-Original-Method / X-Original-URI only when the raw TCP peer (captured before
/// forwarded headers are applied) is in the dedicated EnvoyExtAuthz allowlist (not the shared ForwardAuth/ReverseProxy
/// trusted proxies); every other caller is decided on the route suffix and real method.
/// </summary>
[Trait("Category", "Security")]
public sealed class EnvoyExtAuthzTrustedProxyTests
{
    private static readonly IPAddress Envoy = IPAddress.Parse("10.0.0.5");
    private static readonly IPAddress Attacker = IPAddress.Parse("203.0.113.7");

    private static EnvoyExtAuthzOptions Validator() => new() { TrustedPeers = [Envoy.ToString()] };

    private static DefaultHttpContext Request(IPAddress? rawPeer, IPAddress? connectionIp = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/envoy/check/api/v1/tables/sales";
        context.Request.RouteValues["originalPath"] = "api/v1/tables/sales";
        context.Request.Headers["X-Original-Method"] = "DELETE";
        context.Request.Headers["X-Original-URI"] = "/api/v1/admin/purge";
        context.Connection.RemoteIpAddress = connectionIp ?? rawPeer;
        if (rawPeer != null)
        {
            context.Items[CorrelationIdMiddleware.OriginalTcpRemoteIpItemKey] = rawPeer;
        }

        return context;
    }

    [Fact]
    public void TrustedProxyPeer_HonoursOriginalHeaders()
    {
        var (method, path) = EnvoyExtAuthzEndpoints.ResolveOriginalRequest(Request(Envoy), Validator());

        method.ShouldBe("DELETE");
        path.ShouldBe("/api/v1/admin/purge");
    }

    [Fact]
    public void UntrustedPeer_IgnoresOriginalHeaders_AndUsesRouteSuffixAndRealMethod()
    {
        var (method, path) = EnvoyExtAuthzEndpoints.ResolveOriginalRequest(Request(Attacker), Validator());

        method.ShouldBe("GET");
        path.ShouldBe("/api/v1/tables/sales");
    }

    [Fact]
    public void ForwardedClientIpOfTrustedProxy_DoesNotMakeUntrustedRawPeerTrusted()
    {
        // UseForwardedHeaders may have rewritten Connection.RemoteIpAddress from a spoofed X-Forwarded-For;
        // the decision keys on the raw TCP peer captured before that.
        var (method, path) = EnvoyExtAuthzEndpoints.ResolveOriginalRequest(Request(Attacker, connectionIp: Envoy), Validator());

        method.ShouldBe("GET");
        path.ShouldBe("/api/v1/tables/sales");
    }

    [Fact]
    public void EmptyAllowlist_NeverTrustsOriginalHeaders()
    {
        // Default: no dedicated ext_authz peers configured, even a peer trusted as ForwardAuth/ReverseProxy proxy is ignored.
        var (method, path) = EnvoyExtAuthzEndpoints.ResolveOriginalRequest(Request(Envoy), new EnvoyExtAuthzOptions());

        method.ShouldBe("GET");
        path.ShouldBe("/api/v1/tables/sales");
    }

    [Fact]
    public void NullOptions_NeverTrustsOriginalHeaders()
    {
        var (method, path) = EnvoyExtAuthzEndpoints.ResolveOriginalRequest(Request(Envoy), null);

        method.ShouldBe("GET");
        path.ShouldBe("/api/v1/tables/sales");
    }

    [Fact]
    public void PeerInTrustedNetwork_HonoursOriginalHeaders()
    {
        var options = new EnvoyExtAuthzOptions { TrustedNetworks = ["10.0.0.0/24"] };

        var (method, path) = EnvoyExtAuthzEndpoints.ResolveOriginalRequest(Request(Envoy), options);

        method.ShouldBe("DELETE");
        path.ShouldBe("/api/v1/admin/purge");
    }

    [Fact]
    public void IPv4MappedPeer_IsMatched()
    {
        var options = new EnvoyExtAuthzOptions { TrustedPeers = ["10.0.0.5"] };

        options.IsTrustedPeer(Envoy.MapToIPv6()).ShouldBeTrue();
        options.IsTrustedPeer(Attacker).ShouldBeFalse();
        options.IsTrustedPeer(null).ShouldBeFalse();
    }

    [Fact]
    public void InvalidEntries_AreIgnored()
    {
        var options = new EnvoyExtAuthzOptions { TrustedPeers = ["not-an-ip"], TrustedNetworks = ["garbage/99"] };

        options.IsTrustedPeer(Envoy).ShouldBeFalse();
    }

    [Fact]
    public void UnknownPeer_FailsClosed()
    {
        var (method, path) = EnvoyExtAuthzEndpoints.ResolveOriginalRequest(Request(rawPeer: null), Validator());

        method.ShouldBe("GET");
        path.ShouldBe("/api/v1/tables/sales");
    }
}
