using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Autheris.Api.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;
using AuthenticationOptions = Autheris.Domain.Options.AuthenticationOptions;

namespace Autheris.Tests.Unit.Security;

[Trait("Category", "Security")]
public sealed class ForwardAuthHardeningTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef-secret";

    private static async Task<(AuthenticateResult Result, ForwardAuthAuthenticationHandler Handler)> RunAsync(
        ForwardAuthOptions forward, Action<IHeaderDictionary> headers, string peer = "10.0.0.1", bool development = true, int maxGroups = 1024)
    {
        var options = new GatewayOptions { Authentication = new AuthenticationOptions { Mode = AuthenticationMode.Upstream, ForwardAuth = forward, MaxGroupSidsPerCaller = maxGroups } };
        var env = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.EnvironmentName.Returns(development ? "Development" : "Production");
        var handler = new ForwardAuthAuthenticationHandler(
            new StaticOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            Microsoft.Extensions.Options.Options.Create(options),
            new TrustedProxyValidator(Microsoft.Extensions.Options.Options.Create(options)),
            null,
            env);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        headers(context.Request.Headers);
        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.ForwardAuth, null, typeof(ForwardAuthAuthenticationHandler)), context);
        return (await handler.AuthenticateAsync(), handler);
    }

    private static ForwardAuthOptions Forward() => new() { Enabled = true, TrustedProxies = ["10.0.0.1"] };

    [Theory]
    [InlineData("")]
    [InlineData("alice bob")]
    [InlineData("alice,bob")]
    [InlineData("alice;bob")]
    [InlineData("alïce")]
    [InlineData(" alice")]
    [InlineData("alice\n")]
    public async Task Username_outside_charset_or_length_fails(string username)
    {
        var (result, _) = await RunAsync(Forward(), h => h["X-Forwarded-User"] = username);

        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05b")]
    public async Task Username_length_is_bounded_to_256()
    {
        (await RunAsync(Forward(), h => h["X-Forwarded-User"] = new string('a', 256))).Result.Succeeded.ShouldBeTrue();
        (await RunAsync(Forward(), h => h["X-Forwarded-User"] = new string('a', 257))).Result.Succeeded.ShouldBeFalse();
        (await RunAsync(Forward(), h => h["X-Forwarded-User"] = "DOM\\alice.b@corp-1_x")).Result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05b")]
    public async Task Multiple_username_header_values_fail()
    {
        var (result, _) = await RunAsync(Forward(), h => h["X-Forwarded-User"] = new Microsoft.Extensions.Primitives.StringValues(["alice", "bob"]));

        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05c")]
    public async Task Unicode_username_cannot_collide()
    {
        // KELVIN SIGN (U+212A) lowercases to 'k' under culture and invariant folding; it must be refused instead.
        var (kelvin, _) = await RunAsync(Forward(), h => h["X-Forwarded-User"] = "Kate");
        var (ascii, _) = await RunAsync(Forward(), h => h["X-Forwarded-User"] = "Kate");

        kelvin.Succeeded.ShouldBeFalse();
        ascii.Succeeded.ShouldBeTrue();
        ascii.Principal!.GetUserSid()?.Value.ShouldBe("S-1-5-21-FORWARD-USR-kate");
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05d")]
    public async Task Prefixed_role_for_other_tenant_is_discarded()
    {
        var forward = new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], TrustUpstreamTenant = true, AllowedTenantIds = ["t1", "t2"], AllowedRoles = ["Analyst"] };

        var (result, _) = await RunAsync(forward, h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["X-Forwarded-Tenant"] = "t1";
            h["X-Forwarded-Roles"] = "t2:Analyst,T1:Analyst";
        });

        result.Succeeded.ShouldBeTrue();
        result.Principal!.FindAll(ClaimTypes.Role).Select(c => c.Value).ShouldBe(["T1:Analyst"]);
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05d")]
    public async Task Prefixed_role_without_trusted_tenant_is_discarded()
    {
        var forward = new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], AllowedRoles = ["Analyst"] };

        var (result, _) = await RunAsync(forward, h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["X-Forwarded-Tenant"] = "t1";
            h["X-Forwarded-Roles"] = "t1:Analyst,Analyst";
        });

        result.Succeeded.ShouldBeTrue();
        result.Principal!.FindAll(ClaimTypes.Role).Select(c => c.Value).ShouldBe(["Analyst"]);
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05d")]
    public async Task Role_count_is_bounded()
    {
        var (result, _) = await RunAsync(Forward(), h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["X-Forwarded-Roles"] = string.Join(',', Enumerable.Repeat("Analyst", 33));
        });

        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05a")]
    public async Task Untrusted_peer_fails_even_with_secret()
    {
        var (result, _) = await RunAsync(new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], SharedSecret = Secret }, h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["X-Forwarded-Secret"] = Secret;
        }, peer: "203.0.113.9", development: false);

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Message.ShouldContain("Untrusted proxy");
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05a")]
    public void Reverse_proxy_known_proxies_do_not_trust_identity_headers()
    {
        var options = new GatewayOptions { ReverseProxy = new ReverseProxyOptions { KnownProxies = ["203.0.113.9"] } };
        options.Authentication.ForwardAuth.TrustedProxies.Add("10.0.0.1");
        var validator = new TrustedProxyValidator(Microsoft.Extensions.Options.Options.Create(options));

        validator.IsForwardAuthPeerTrusted(IPAddress.Parse("10.0.0.1")).ShouldBeTrue();
        validator.IsForwardAuthPeerTrusted(IPAddress.Parse("203.0.113.9")).ShouldBeFalse();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05c")]
    public async Task Non_canonical_user_sid_fails_request()
    {
        var forward = new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], UserSidHeader = "X-Forwarded-Sid", TrustUpstreamSids = true };

        var (result, _) = await RunAsync(forward, h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["X-Forwarded-Sid"] = "S-1-5-21-FORWARD-USR-alice";
        });
        var (valid, _) = await RunAsync(forward, h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["X-Forwarded-Sid"] = "S-1-5-21-1-2-3-1001";
        });

        result.Succeeded.ShouldBeFalse();
        valid.Succeeded.ShouldBeTrue();
        valid.Principal!.GetUserSid()?.Value.ShouldBe("S-1-5-21-1-2-3-1001");
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05c")]
    public async Task User_sid_ignored_without_trust_upstream_sids()
    {
        var forward = new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], UserSidHeader = "X-Forwarded-Sid" };

        var (result, _) = await RunAsync(forward, h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["X-Forwarded-Sid"] = "S-1-5-21-1-2-3-500";
        });

        result.Succeeded.ShouldBeTrue();
        result.Principal!.GetUserSid()?.Value.ShouldBe("S-1-5-21-FORWARD-USR-alice");
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-02c")]
    public async Task Upstream_principal_carries_no_authorization_derived_claim()
    {
        var (result, _) = await RunAsync(Forward(), h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["Authorization"] = "Bearer token-marker-value";
        });

        result.Succeeded.ShouldBeTrue();
        result.Principal!.Claims.Select(c => c.Value).ShouldAllBe(v => !v.Contains("token-marker-value"));
        result.Principal.Claims.ShouldNotContain(c => c.Type == "access_token" || c.Type == "authorization");
        result.Principal.GetUserSid()?.Value.ShouldBe("S-1-5-21-FORWARD-USR-alice");
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05f")]
    public async Task Header_groups_must_be_canonical_sids()
    {
        var forward = new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], TrustUpstreamSids = true, GroupSource = ForwardAuthGroupSource.Header };

        var (valid, _) = await RunAsync(forward, h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["X-Forwarded-Groups"] = "S-1-5-21-1-2-3-1105,S-1-5-21-1-2-3-1106";
        });
        var (prefixOnly, _) = await RunAsync(forward, h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["X-Forwarded-Groups"] = "S-1-5-21-1-2-3-1105,S-1-5-finance";
        });
        var (pseudo, _) = await RunAsync(forward, h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["X-Forwarded-Groups"] = "finance";
        });

        valid.Succeeded.ShouldBeTrue();
        valid.Principal!.GetGroupSids().Select(g => g.Value).ShouldBe(["S-1-5-21-1-2-3-1105", "S-1-5-21-1-2-3-1106"], ignoreOrder: true);
        prefixOnly.Succeeded.ShouldBeFalse();
        pseudo.Succeeded.ShouldBeFalse();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05f")]
    public async Task Header_groups_are_ignored_without_trust_upstream_sids()
    {
        var (result, _) = await RunAsync(Forward(), h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["X-Forwarded-Groups"] = "S-1-5-21-1-2-3-1105,finance";
        });

        result.Succeeded.ShouldBeTrue();
        result.Principal!.GetGroupSids().ShouldBeEmpty();
    }

    private static ForwardAuthOptions HeaderGroups() =>
        new() { Enabled = true, TrustedProxies = ["10.0.0.1"], TrustUpstreamSids = true, GroupSource = ForwardAuthGroupSource.Header };

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05f")]
    public async Task Resolver_mode_ignores_group_header()
    {
        var forward = new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], TrustUpstreamSids = true };

        var (result, _) = await RunAsync(forward, h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["X-Forwarded-Groups"] = "S-1-5-21-1-2-3-1105,finance";
        });

        result.Succeeded.ShouldBeTrue();
        result.Principal!.GetGroupSids().ShouldBeEmpty();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05f")]
    public async Task Header_group_count_over_cap_fails()
    {
        var (atCap, _) = await RunAsync(HeaderGroups(), h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["X-Forwarded-Groups"] = "S-1-5-21-1-2-3-1105,S-1-5-21-1-2-3-1106";
        }, maxGroups: 2);
        var (overCap, _) = await RunAsync(HeaderGroups(), h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["X-Forwarded-Groups"] = "S-1-5-21-1-2-3-1105,S-1-5-21-1-2-3-1106,S-1-5-21-1-2-3-1107";
        }, maxGroups: 2);

        atCap.Succeeded.ShouldBeTrue();
        overCap.Succeeded.ShouldBeFalse();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05f")]
    public async Task Pseudo_forward_group_form_is_rejected()
    {
        var (result, _) = await RunAsync(HeaderGroups(), h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["X-Forwarded-Groups"] = "S-1-5-21-FORWARD-GRP-finance";
        });

        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05f")]
    public async Task Joined_group_headers_fail()
    {
        var (result, _) = await RunAsync(HeaderGroups(), h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["X-Forwarded-Groups"] = new Microsoft.Extensions.Primitives.StringValues(["S-1-5-21-1-2-3-1105", "S-1-5-21-1-2-3-1106"]);
        });

        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05f")]
    public async Task Forwarded_groups_ignored_unless_trusted_upstream()
    {
        // GroupSource=Header without TrustUpstreamSids: the header is not read; from an untrusted peer the request fails even with the secret.
        var noTrust = new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], GroupSource = ForwardAuthGroupSource.Header };

        var (ignored, _) = await RunAsync(noTrust, h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["X-Forwarded-Groups"] = "S-1-5-21-1-2-3-1105";
        });
        var (untrusted, _) = await RunAsync(HeaderGroups(), h =>
        {
            h["X-Forwarded-User"] = "alice";
            h["X-Forwarded-Groups"] = "S-1-5-21-1-2-3-1105";
        }, peer: "10.9.9.9");

        ignored.Succeeded.ShouldBeTrue();
        ignored.Principal!.GetGroupSids().ShouldBeEmpty();
        untrusted.Succeeded.ShouldBeFalse();
    }
}
