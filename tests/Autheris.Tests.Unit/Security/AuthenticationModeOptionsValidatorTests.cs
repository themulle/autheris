using Autheris.Api.Configuration.Validation;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Identity;
using Autheris.Domain.Identity;
using Autheris.Domain.Options.Security;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

[Trait("Category", "Security")]
public sealed class AuthenticationModeOptionsValidatorTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef-secret";

    private static IHostEnvironment Env(string name = "Production")
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static GatewayOptions Upstream(Func<ForwardAuthOptions, ForwardAuthOptions>? fwd = null, AuthenticationMode mode = AuthenticationMode.Upstream)
    {
        var forward = new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], SharedSecret = Secret };
        return new GatewayOptions { Authentication = new AuthenticationOptions { Mode = mode, ForwardAuth = fwd?.Invoke(forward) ?? forward } };
    }

    private static string Failure(GatewayOptions options, string env = "Production", IKeyVaultSecretProvider? provider = null) =>
        new AuthenticationModeOptionsValidator(Env(env), provider).Validate(null, options) is { Failed: true } r ? string.Join("|", r.Failures!) : string.Empty;

    [Fact]
    public void Valid_upstream_configuration_passes() => Failure(Upstream()).ShouldBeEmpty();

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-01a")]
    public void Upstream_without_trusted_peers_fails() =>
        Failure(Upstream(f => new ForwardAuthOptions { Enabled = true, SharedSecret = Secret })).ShouldContain("ForwardAuth:TrustedProxies or TrustedNetworks");

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-01a")]
    public void Upstream_without_secret_fails() =>
        Failure(Upstream(f => new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"] })).ShouldContain("SharedSecret or SharedSecretKeyVaultRef");

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-01b")]
    public void Short_secret_fails_at_startup() =>
        Failure(Upstream(f => new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], SharedSecret = "short" })).ShouldContain("at least 32 bytes");

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-01b")]
    public void Secret_length_is_measured_in_utf8_bytes() =>
        Failure(Upstream(f => new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], SharedSecret = new string('é', 16) })).ShouldBeEmpty();

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-01a")]
    public void Secret_unresolvable_fails_at_startup()
    {
        var provider = Substitute.For<IKeyVaultSecretProvider>();
        provider.GetSecretBytes(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("vault unreachable " + Secret));
        var options = Upstream(f => new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], SharedSecretKeyVaultRef = "kv-ref" });

        var failure = Failure(options, provider: provider);

        failure.ShouldContain("SharedSecretKeyVaultRef could not be resolved");
        failure.ShouldNotContain(Secret);
        Failure(options, provider: null).ShouldContain("could not be resolved");
        provider.GetSecretBytes(Arg.Any<string>()).Returns([]);
        Failure(options, provider: provider).ShouldContain("could not be resolved");
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-01c")]
    public void Direct_with_forwardauth_enabled_fails() =>
        Failure(Upstream(mode: AuthenticationMode.Direct)).ShouldContain("Mode=Direct requires Gateway:Authentication:ForwardAuth:Enabled=false");

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-01c")]
    public void Upstream_with_direct_user_scheme_fails()
    {
        var forward = Upstream().Authentication.ForwardAuth;
        AuthenticationOptions[] direct =
        [
            new() { Mode = AuthenticationMode.Upstream, ForwardAuth = forward, EntraId = new EntraIdAuthOptions { Enabled = true } },
            new() { Mode = AuthenticationMode.Upstream, ForwardAuth = forward, Adfs = new AdfsAuthOptions { Enabled = true } },
            new() { Mode = AuthenticationMode.Upstream, ForwardAuth = forward, BasicAuth = new BasicAuthOptions { Enabled = true } },
            new() { Mode = AuthenticationMode.Upstream, ForwardAuth = forward, EnableKerberos = true }
        ];

        foreach (var auth in direct)
        {
            Failure(new GatewayOptions { Authentication = auth }).ShouldContain("Mode=Upstream requires EntraId, Adfs, BasicAuth and EnableKerberos to be disabled");
        }

        var both = new AuthenticationOptions { Mode = AuthenticationMode.Both, ForwardAuth = forward, EntraId = new EntraIdAuthOptions { Enabled = true } };
        Failure(new GatewayOptions { Authentication = both }).ShouldBeEmpty();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-01c")]
    public void ForwardAuth_without_explicit_mode_fails()
    {
        var options = Upstream(mode: AuthenticationMode.Unset);

        Failure(options).ShouldContain("Gateway:Authentication:Mode must be set explicitly");
        Failure(options, "Development").ShouldContain("Gateway:Authentication:Mode must be set explicitly");
    }

    [Fact]
    public void Direct_without_any_scheme_fails_outside_development()
    {
        var options = new GatewayOptions { Authentication = new AuthenticationOptions { Mode = AuthenticationMode.Direct } };

        Failure(options).ShouldContain("Mode=Direct requires at least one enabled scheme");
        Failure(options, "Development").ShouldBeEmpty();
        Failure(new GatewayOptions { Authentication = new AuthenticationOptions { Mode = AuthenticationMode.Direct, EnableTestAuthHandler = true } }).ShouldBeEmpty();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-BASIC")]
    public void Basic_user_with_non_canonical_group_sid_fails()
    {
        var user = new BasicAuthUserConfig { Username = "u", Sid = "S-1-5-21-1-2", GroupSids = ["S-1-5-21-1-513", "S-1-5-21-FORWARD-GRP-x"] };
        var options = new GatewayOptions { Authentication = new AuthenticationOptions { Mode = AuthenticationMode.Direct, BasicAuth = new BasicAuthOptions { Enabled = true, Users = [user] } } };

        var failure = Failure(options, "Development");

        failure.ShouldContain("BasicAuth:Users:0");
        failure.ShouldNotContain("FORWARD-GRP");
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05e")]
    public void Trust_upstream_tenant_without_allowed_tenants_fails() =>
        Failure(Upstream(f => new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], SharedSecret = Secret, TrustUpstreamTenant = true }))
            .ShouldContain("TrustUpstreamTenant requires a non-empty");

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-01a")]
    public void Failure_messages_never_contain_secret_value()
    {
        var options = Upstream(f => new ForwardAuthOptions { Enabled = true, SharedSecret = Secret[..10], RequireTrustedProxy = false });

        var failure = Failure(options);

        failure.ShouldNotBeNull();
        failure.ShouldNotContain(Secret[..10]);
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-ALIAS")]
    public void Legacy_trust_key_conflicting_with_new_key_fails()
    {
        Failure(Upstream(f => new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], SharedSecret = Secret, TrustUpstreamSids = false, TrustUpstreamGroupSids = true }))
            .ShouldContain("TrustUpstreamGroupSids (legacy alias)");
        Failure(Upstream(f => new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], SharedSecret = Secret, TrustUpstreamSids = true, TrustUpstreamGroupSids = true })).ShouldBeEmpty();
        new ForwardAuthOptions { TrustUpstreamGroupSids = true }.IsUpstreamSidTrusted.ShouldBeTrue();
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-02b")]
    public void Blank_header_name_fails()
    {
        Failure(Upstream(f => new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], SharedSecret = Secret, RolesHeader = " " }))
            .ShouldContain("ForwardAuth:RolesHeader must not be empty");
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-02b")]
    public void Duplicate_header_names_fail_startup() =>
        Failure(Upstream(f => new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], SharedSecret = Secret, GroupsHeader = "x-forwarded-roles" }))
            .ShouldContain("duplicates another ForwardAuth header name");

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-05f")]
    public void Trust_upstream_sids_without_header_group_source_fails()
    {
        var withoutHeader = Upstream(f => new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], SharedSecret = Secret, TrustUpstreamSids = true });
        var legacyAlias = Upstream(f => new ForwardAuthOptions { Enabled = true, TrustedProxies = ["10.0.0.1"], SharedSecret = Secret, TrustUpstreamGroupSids = true });
        var withHeader = Upstream(f => new ForwardAuthOptions
        {
            Enabled = true, TrustedProxies = ["10.0.0.1"], SharedSecret = Secret, TrustUpstreamSids = true, GroupSource = ForwardAuthGroupSource.Header
        });

        Failure(withoutHeader).ShouldContain("TrustUpstreamSids requires Gateway:Authentication:ForwardAuth:GroupSource=Header");
        Failure(legacyAlias).ShouldContain("GroupSource=Header");
        Failure(withHeader).ShouldBeEmpty();
    }

    private static IRoleProvider Provider(string id)
    {
        var provider = Substitute.For<IRoleProvider>();
        provider.ProviderId.Returns(id);
        return provider;
    }

    private static IOptions<IdentityRealmsOptions> Realms(IdentityRealmKind kind, RealmGroupSource source, params string[] chain) =>
        Microsoft.Extensions.Options.Options.Create(new IdentityRealmsOptions
        {
            Realms = new Dictionary<string, IdentityRealmConfigOptions>
            {
                ["kdc"] = new() { Kind = kind, GroupSource = source, ResolverChain = [.. chain] }
            }
        });

    private static string KerberosFailure(IOptions<IdentityRealmsOptions>? realms, IEnumerable<IRoleProvider>? providers, bool windows)
    {
        var options = new GatewayOptions { Authentication = new AuthenticationOptions { Mode = AuthenticationMode.Direct, EnableKerberos = true } };
        return new AuthenticationModeOptionsValidator(Env(), null, null, realms, providers, windows).Validate(null, options) is { Failed: true } r
            ? string.Join("|", r.Failures!)
            : string.Empty;
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-01c")]
    public void Kerberos_on_linux_without_ldaps_resolver_fails()
    {
        var ldaps = new[] { Provider("ldaps-ad") };

        KerberosFailure(null, null, windows: false).ShouldContain("EnableKerberos on a non-Windows host");
        KerberosFailure(Realms(IdentityRealmKind.Kerberos, RealmGroupSource.Resolver, "ldaps-ad"), null, windows: false).ShouldContain("EnableKerberos");
        KerberosFailure(Realms(IdentityRealmKind.Kerberos, RealmGroupSource.Token, "ldaps-ad"), ldaps, windows: false).ShouldContain("EnableKerberos");
        KerberosFailure(Realms(IdentityRealmKind.Kerberos, RealmGroupSource.Resolver, "scim-store"), ldaps, windows: false).ShouldContain("EnableKerberos");
        KerberosFailure(Realms(IdentityRealmKind.EntraWorkforce, RealmGroupSource.Resolver, "ldaps-ad"), ldaps, windows: false).ShouldContain("EnableKerberos");
        KerberosFailure(Realms(IdentityRealmKind.Kerberos, RealmGroupSource.Resolver, "ldaps-ad"), ldaps, windows: false).ShouldBeEmpty();
        KerberosFailure(null, null, windows: true).ShouldBeEmpty();
    }
}
