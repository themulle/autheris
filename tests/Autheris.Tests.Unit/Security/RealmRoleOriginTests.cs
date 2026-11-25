namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Caching;
using Autheris.Application.Identity.Providers;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using AuthenticationOptions = Autheris.Domain.Options.AuthenticationOptions;

[Trait("Category", "Security")]
public sealed class RealmRoleOriginTests
{
    private static readonly TenantId TenantA = new("tenant-a");

    private static IdentityRealm CreateRealm(
        string id,
        IdentityRealmKind kind,
        RealmGroupSource groupSource,
        params GatewayRole[] allowedGatewayRoles) =>
        new(
            id,
            kind,
            TenantBindingSpecification.Fixed(TenantA),
            groupSource: groupSource,
            resolverChain: ["static-mapping"],
            provisioning: RealmProvisioningMode.None,
            allowedGatewayRoles: new HashSet<GatewayRole>(allowedGatewayRoles));

    [Fact]
    public async Task ForwardAuth_identity_gets_no_it_or_governance_role_unless_realm_allows()
    {
        // 1. ForwardAuth realm permitting only Consumer: resolver-returned TenantAdmin, GovernanceAdmin, and ClusterAdmin must all be dropped.
        var restrictedRealm = CreateRealm("forward-restricted", IdentityRealmKind.ForwardAuth, RealmGroupSource.Resolver, GatewayRole.Consumer);
        var resolver = Substitute.For<IRoleResolver>();
        TestIdentityPipeline.StubResolution(
            resolver,
            new RoleResolution(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ClusterAdmin", "TenantAdmin", "GovernanceAdmin", "Consumer" },
                new Dictionary<string, string>(),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5),
                "resolved"));

        var restrictedRegistry = new FakeIdentityRealmRegistry(restrictedRealm);
        var restrictedPipeline = TestIdentityPipeline.Create(restrictedRegistry, resolver);
        var rawForwardAuth = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "alice"),
            new Claim("oid", "alice-oid"),
            RoleResolutionClaims.CreateAllowedGatewayRolesClaim([GatewayRole.ClusterAdmin, GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin])
        ], ClusterAdminPolicy.ForwardAuthScheme));

        var restrictedPrincipal = await restrictedPipeline.ProcessAsync(rawForwardAuth, ClusterAdminPolicy.ForwardAuthScheme, null, CancellationToken.None);

        var evaluator = new GatewayRoleEvaluator(restrictedRegistry);
        restrictedPrincipal.GetUserRoles().ShouldContain("Consumer");
        restrictedPrincipal.GetUserRoles().ShouldNotContain("TenantAdmin");
        restrictedPrincipal.GetUserRoles().ShouldNotContain("GovernanceAdmin");
        restrictedPrincipal.GetUserRoles().ShouldNotContain("ClusterAdmin");

        evaluator.HasRole(restrictedPrincipal, GatewayRole.Consumer, TenantA.Value).ShouldBeTrue();
        evaluator.HasRole(restrictedPrincipal, GatewayRole.TenantAdmin, TenantA.Value).ShouldBeFalse();
        evaluator.HasRole(restrictedPrincipal, GatewayRole.GovernanceAdmin, TenantA.Value).ShouldBeFalse();
        evaluator.HasRole(restrictedPrincipal, GatewayRole.ClusterAdmin).ShouldBeFalse();
        GatewayPolicies.HasRole(restrictedPrincipal, GatewayRole.TenantAdmin, TenantA.Value).ShouldBeFalse();
        GatewayPolicies.HasRole(restrictedPrincipal, GatewayRole.GovernanceAdmin, TenantA.Value).ShouldBeFalse();
        GatewayPolicies.HasRole(restrictedPrincipal, GatewayRole.ClusterAdmin).ShouldBeFalse();

        // Even an unforged pipeline AllowedGatewayRoles claim was replaced (not kept from inbound forgery).
        RoleResolutionClaims.TryReadAllowedGatewayRoles(restrictedPrincipal, out var stampedAllowed).ShouldBeTrue();
        stampedAllowed.ShouldBe([GatewayRole.Consumer]);

        // 2. ForwardAuth realm explicitly allowing TenantAdmin & GovernanceAdmin (and even misconfigured ClusterAdmin):
        //    TenantAdmin & GovernanceAdmin are allowed via resolver, while ClusterAdmin is NEVER honoured on ForwardAuth.
        var permittedRealm = CreateRealm(
            "forward-permitted",
            IdentityRealmKind.ForwardAuth,
            RealmGroupSource.Resolver,
            GatewayRole.Consumer,
            GatewayRole.TenantAdmin,
            GatewayRole.GovernanceAdmin,
            GatewayRole.ClusterAdmin);
        var permittedRegistry = new FakeIdentityRealmRegistry(permittedRealm);
        var permittedPipeline = TestIdentityPipeline.Create(permittedRegistry, resolver);

        var permittedPrincipal = await permittedPipeline.ProcessAsync(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice"), new Claim("oid", "alice-oid")], ClusterAdminPolicy.ForwardAuthScheme)),
            ClusterAdminPolicy.ForwardAuthScheme,
            null,
            CancellationToken.None);

        var permittedEvaluator = new GatewayRoleEvaluator(permittedRegistry);
        permittedEvaluator.HasRole(permittedPrincipal, GatewayRole.TenantAdmin, TenantA.Value).ShouldBeTrue();
        GatewayPolicies.HasRole(permittedPrincipal, GatewayRole.GovernanceAdmin, TenantA.Value).ShouldBeTrue();
        permittedEvaluator.HasRole(permittedPrincipal, GatewayRole.ClusterAdmin).ShouldBeFalse();
        ClusterAdminPolicy.IsCanonicalClusterAdmin(permittedPrincipal).ShouldBeFalse();

        // 3. ForwardAuthAuthenticationHandler itself enforces AllowedGatewayRoles on header roles and always forbids admin headers.
        var forwardOptions = new GatewayOptions
        {
            Authentication = new AuthenticationOptions
            {
                Mode = AuthenticationMode.Upstream,
                ForwardAuth = new ForwardAuthOptions
                {
                    Enabled = true,
                    RequireTrustedProxy = true,
                    TrustedProxies = ["10.0.0.1"],
                    AllowedRoles = ["Consumer", "SchemaPublisher", "TenantAdmin", "GovernanceAdmin", "ClusterAdmin"]
                }
            }
        };
        var handlerRealm = CreateRealm("forward-header", IdentityRealmKind.ForwardAuth, RealmGroupSource.None, GatewayRole.Consumer);
        var env = Substitute.For<IWebHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var handler = new ForwardAuthAuthenticationHandler(
            new StaticOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            Options.Create(forwardOptions),
            new TrustedProxyValidator(Options.Create(forwardOptions)),
            secretProvider: null,
            environment: env,
            realmRegistry: new FakeIdentityRealmRegistry(handlerRealm));
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
        httpContext.Request.Headers["X-Forwarded-User"] = "alice";
        httpContext.Request.Headers["X-Forwarded-Roles"] = "Consumer,SchemaPublisher,TenantAdmin,GovernanceAdmin,ClusterAdmin";
        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.ForwardAuth, null, typeof(ForwardAuthAuthenticationHandler)), httpContext);

        var authResult = await handler.AuthenticateAsync();
        authResult.Succeeded.ShouldBeTrue();
        authResult.Principal!.GetUserRoles().ShouldBe(["Consumer"]);

        // 4. JWT / Basic token roles outside realm AllowedGatewayRoles are dropped by pipeline and GatewayRoleEvaluator.
        var jwtRealm = CreateRealm("oidc-consumer", IdentityRealmKind.GenericOidc, RealmGroupSource.Token, GatewayRole.Consumer);
        var jwtRegistry = new FakeIdentityRealmRegistry(jwtRealm);
        var jwtPipeline = TestIdentityPipeline.Create(jwtRegistry);
        var jwtRaw = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "user-1"),
            new Claim("oid", "user-1"),
            new Claim(ClaimTypes.Role, "Consumer"),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim(ClaimTypes.Role, "tenant-a:TenantAdmin"),
            new Claim(ClaimTypes.Role, "ClusterAdmin")
        ], "Bearer"));

        var jwtProcessed = await jwtPipeline.ProcessAsync(jwtRaw, "Bearer", null, CancellationToken.None);
        var jwtEvaluator = new GatewayRoleEvaluator(jwtRegistry);
        jwtProcessed.GetUserRoles().ShouldBe(["Consumer"]);
        jwtEvaluator.HasRole(jwtProcessed, GatewayRole.Consumer, TenantA.Value).ShouldBeTrue();
        jwtEvaluator.HasRole(jwtProcessed, GatewayRole.GovernanceAdmin, TenantA.Value).ShouldBeFalse();
        jwtEvaluator.HasRole(jwtProcessed, GatewayRole.TenantAdmin, TenantA.Value).ShouldBeFalse();
        jwtEvaluator.HasRole(jwtProcessed, GatewayRole.ClusterAdmin).ShouldBeFalse();
        ClusterAdminPolicy.IsCanonicalClusterAdmin(jwtProcessed).ShouldBeFalse();
    }

    [Fact]
    public async Task Group_resolver_role_outside_allowed_set_dropped()
    {
        var realm = CreateRealm(
            "corp-entra",
            IdentityRealmKind.EntraWorkforce,
            RealmGroupSource.Resolver,
            GatewayRole.Consumer,
            GatewayRole.SecurityAuditor);

        var provider = new StaticMappingRoleProvider();
        provider.AddMapping(
            "user-1",
            groups: ["S-1-5-21-1-2-3-5001"],
            roles: ["Consumer", "tenant-a:SecurityAuditor", "GovernanceAdmin", "TenantAdmin", "ClusterAdmin"]);

        var customProvider = Substitute.For<IRoleProvider>();
        customProvider.ProviderId.Returns("custom-ext");
        customProvider.GetRolesAsync(Arg.Any<ResolvedIdentity>(), Arg.Any<IdentityRealm>(), Arg.Any<CancellationToken>())
            .Returns(new ProviderRoleResult(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SchemaPublisher", "NonCanonicalRole" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

        var resolverRealm = realm with { ResolverChain = [StaticMappingRoleProvider.Id, "custom-ext"] };
        var registry = new FakeIdentityRealmRegistry(resolverRealm);
        var roleResolver = new RoleResolver(registry, [provider, customProvider], new RoleResolverCache());

        var identity = new ResolvedIdentity(
            "user-1",
            "user1@corp.example",
            "User One",
            "user1@corp.example",
            TenantA,
            resolverRealm.Id,
            []);

        var resolution = await roleResolver.ResolveRolesAsync(identity, CancellationToken.None);

        resolution.IsDegraded.ShouldBeFalse();
        resolution.Groups.ShouldBe(["S-1-5-21-1-2-3-5001"]);
        resolution.Roles.ShouldContain("Consumer");
        resolution.Roles.ShouldContain("tenant-a:SecurityAuditor");
        resolution.Roles.ShouldNotContain("GovernanceAdmin");
        resolution.Roles.ShouldNotContain("TenantAdmin");
        resolution.Roles.ShouldNotContain("ClusterAdmin");
        resolution.Roles.ShouldNotContain("SchemaPublisher");
        resolution.Roles.ShouldNotContain("NonCanonicalRole");

        // End-to-end through GroupSidResolver + IdentityPipeline
        var options = Options.Create(new GatewayOptions());
        var groupResolver = new GroupSidResolver(roleResolver, [provider, customProvider], options);
        var pipeline = new IdentityPipeline(registry, groupResolver, roleResolver, options: options);
        var raw = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "user-1"), new Claim("sub", "user-1")], "Bearer"));

        var processed = await pipeline.ProcessAsync(raw, "Bearer", null, CancellationToken.None);
        var evaluator = new GatewayRoleEvaluator(registry);

        processed.GetGroupSids().Select(g => g.Value).ShouldBe(["S-1-5-21-1-2-3-5001"]);
        evaluator.HasRole(processed, GatewayRole.Consumer, TenantA.Value).ShouldBeTrue();
        evaluator.HasRole(processed, GatewayRole.SecurityAuditor, TenantA.Value).ShouldBeTrue();
        evaluator.HasRole(processed, GatewayRole.GovernanceAdmin, TenantA.Value).ShouldBeFalse();
        evaluator.HasRole(processed, GatewayRole.TenantAdmin, TenantA.Value).ShouldBeFalse();
        evaluator.HasRole(processed, GatewayRole.ClusterAdmin).ShouldBeFalse();
        evaluator.HasRole(processed, GatewayRole.SchemaPublisher, TenantA.Value).ShouldBeFalse();
    }
}
