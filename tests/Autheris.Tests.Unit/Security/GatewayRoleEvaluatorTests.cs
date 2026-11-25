namespace Autheris.Tests.Unit.Security;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Identity;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class GatewayRoleEvaluatorTests
{
    private static ClaimsPrincipal MakePrincipal(
        string scheme = "Bearer",
        string? tenantId = null,
        IEnumerable<GatewayRole>? allowedGatewayRoles = null,
        params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-EVAL") };
        if (tenantId is not null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (allowedGatewayRoles is not null)
        {
            claims.Add(RoleResolutionClaims.CreateAllowedGatewayRolesClaim(allowedGatewayRoles));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }

    [Fact]
    public void GetEffectiveRoles_ExcludesPrefixedAdminRoles_Wildcards_AndUnprefixedProductRoles()
    {
        var evaluator = new GatewayRoleEvaluator();

        // t:GovernanceAdmin and t:ClusterAdmin are never returned by GetEffectiveRoles.
        var prefixedAdmins = MakePrincipal(tenantId: "tenant-a", roles: ["tenant-a:GovernanceAdmin", "tenant-a:ClusterAdmin"]);
        evaluator.GetEffectiveRoles(prefixedAdmins).ShouldBeEmpty();
        evaluator.GetEffectiveRoles(prefixedAdmins, "tenant-a").ShouldBeEmpty();

        // Unprefixed DataProduct* and wildcard *:DataProduct* never confer product roles.
        var invalidProducts = MakePrincipal(tenantId: "tenant-a", roles: ["DataProductAdmin", "DataProductSync", "*:DataProductAdmin", "*:DataProductSync"]);
        evaluator.GetEffectiveRoles(invalidProducts).ShouldBeEmpty();
        evaluator.GetEffectiveRoles(invalidProducts, "tenant-a").ShouldBeEmpty();

        // Tenant-prefixed TenantAdmin and DataProduct* resolve only for the matching explicit tenantId.
        var tenantScoped = MakePrincipal(tenantId: "tenant-a", roles: ["tenant-a:TenantAdmin", "tenant-a:DataProductAdmin", "tenant-a:DataProductSync"]);
        evaluator.GetEffectiveRoles(tenantScoped).ShouldBeEmpty();
        evaluator.GetEffectiveRoles(tenantScoped, "tenant-b").ShouldBeEmpty();
        evaluator.GetEffectiveRoles(tenantScoped, "tenant-a").ShouldBe(
            [GatewayRole.TenantAdmin, GatewayRole.DataProductAdmin, GatewayRole.DataProductSync],
            ignoreOrder: true);
    }

    [Fact]
    public void GetEffectiveRoles_PreservesForwardAuthClusterAdminBlock_AndAllowedGatewayRolesIntersection()
    {
        var evaluator = new GatewayRoleEvaluator();

        var forwardAuth = MakePrincipal(
            scheme: ClusterAdminPolicy.ForwardAuthScheme,
            tenantId: "tenant-a",
            allowedGatewayRoles: [GatewayRole.ClusterAdmin, GatewayRole.GovernanceAdmin],
            roles: ["ClusterAdmin", "GovernanceAdmin"]);

        var effective = evaluator.GetEffectiveRoles(forwardAuth, "tenant-a");
        effective.ShouldBe([GatewayRole.GovernanceAdmin]);
        effective.ShouldNotContain(GatewayRole.ClusterAdmin);

        var restricted = MakePrincipal(
            tenantId: "tenant-a",
            allowedGatewayRoles: [GatewayRole.Consumer],
            roles: ["GovernanceAdmin", "Consumer"]);
        evaluator.GetEffectiveRoles(restricted, "tenant-a").ShouldBe([GatewayRole.Consumer]);
    }

    [Fact]
    public void HasTenantBoundRole_FailsClosedOnInvalidOrForeignTenant_AndAllowsMatchingPrefixOrClaim()
    {
        var evaluator = new GatewayRoleEvaluator();

        // Invalid tenant claim fails closed even with GovernanceAdmin or matching prefix.
        var invalidClaim = MakePrincipal(tenantId: "evil<script>tenant", roles: ["GovernanceAdmin", "tenant-a:GovernanceAdmin"]);
        GatewayRoleEvaluator.HasTenantBoundRole(invalidClaim, nameof(GatewayRole.GovernanceAdmin), "tenant-a").ShouldBeFalse();
        GatewayRoleEvaluator.HasTenantBoundRole(invalidClaim, nameof(GatewayRole.GovernanceAdmin), null).ShouldBeFalse();

        // Foreign tenant mismatch fails closed.
        var foreignClaim = MakePrincipal(tenantId: "tenant-a", roles: ["GovernanceAdmin", "tenant-b:GovernanceAdmin"]);
        GatewayRoleEvaluator.HasTenantBoundRole(foreignClaim, nameof(GatewayRole.GovernanceAdmin), "tenant-b").ShouldBeFalse();
        GatewayRoleEvaluator.HasTenantBoundRole(foreignClaim, nameof(GatewayRole.GovernanceAdmin), "tenant-a").ShouldBeTrue();
        GatewayRoleEvaluator.HasTenantBoundRole(foreignClaim, nameof(GatewayRole.GovernanceAdmin), null).ShouldBeTrue();

        // Matching t:Role works when claim matches or claim is absent and explicit tenant matches.
        var prefixedClaim = MakePrincipal(tenantId: "tenant-a", roles: "tenant-a:GovernanceAdmin");
        GatewayRoleEvaluator.HasTenantBoundRole(prefixedClaim, nameof(GatewayRole.GovernanceAdmin), null).ShouldBeTrue();
        GatewayRoleEvaluator.HasTenantBoundRole(prefixedClaim, nameof(GatewayRole.GovernanceAdmin), "tenant-a").ShouldBeTrue();
        GatewayRoleEvaluator.HasTenantBoundRole(prefixedClaim, nameof(GatewayRole.GovernanceAdmin), "tenant-b").ShouldBeFalse();

        var prefixedNoClaim = MakePrincipal(tenantId: null, roles: "tenant-a:TenantAdmin");
        GatewayRoleEvaluator.HasTenantBoundRole(prefixedNoClaim, nameof(GatewayRole.TenantAdmin), "tenant-a").ShouldBeTrue();
        GatewayRoleEvaluator.HasTenantBoundRole(prefixedNoClaim, nameof(GatewayRole.TenantAdmin), "tenant-b").ShouldBeFalse();
        GatewayRoleEvaluator.HasTenantBoundRole(prefixedNoClaim, nameof(GatewayRole.TenantAdmin), null).ShouldBeFalse();

        // Wildcard *:Role never matches HasTenantBoundRole.
        var wildcard = MakePrincipal(tenantId: "tenant-a", roles: "*:GovernanceAdmin");
        GatewayRoleEvaluator.HasTenantBoundRole(wildcard, nameof(GatewayRole.GovernanceAdmin), "tenant-a").ShouldBeFalse();

        // ForwardAuth ClusterAdmin and disallowed roles fail closed.
        var forwardCluster = MakePrincipal(scheme: ClusterAdminPolicy.ForwardAuthScheme, tenantId: "tenant-a", roles: "ClusterAdmin");
        GatewayRoleEvaluator.HasTenantBoundRole(forwardCluster, nameof(GatewayRole.ClusterAdmin), "tenant-a").ShouldBeFalse();

        var disallowed = MakePrincipal(tenantId: "tenant-a", allowedGatewayRoles: [GatewayRole.Consumer], roles: "GovernanceAdmin");
        GatewayRoleEvaluator.HasTenantBoundRole(disallowed, nameof(GatewayRole.GovernanceAdmin), "tenant-a").ShouldBeFalse();

        _ = evaluator;
    }

#if AUTHERIS_TEST_AUTH
    [Fact]
    public async Task TestAuth_ThroughIdentityPipeline_WithUnregisteredDefaultRealm_PreservesRoles()
    {
        var registry = new IdentityRealmRegistry();
        var options = Options.Create(new GatewayOptions());
        var pipeline = new IdentityPipeline(
            registry,
            new GroupSidResolver(null, [], options),
            options: options);

        var raw = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-TEST-ADMIN"),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim("tenant_id", "warehouse")
        ], TestAuthHandler.SchemeName));

        var processed = await pipeline.ProcessAsync(raw, TestAuthHandler.SchemeName, null, CancellationToken.None);
        var evaluator = new GatewayRoleEvaluator(registry);

        evaluator.HasRole(processed, GatewayRole.GovernanceAdmin, "warehouse").ShouldBeTrue();
        GatewayPolicies.HasRole(processed, GatewayRole.GovernanceAdmin, "warehouse").ShouldBeTrue();
        GatewayPolicies.HasRole(processed, GatewayRole.GovernanceAdmin, "other-tenant").ShouldBeFalse();
    }
#endif

    [Fact]
    public void GetEffectiveRoles_And_HasExactRole_RejectWildcards_AndValidateTenantClaims()
    {
        var evaluator = new GatewayRoleEvaluator();

        // Wildcard *:Role must never satisfy GetEffectiveRoles, HasRole, or HasExactRole.
        var wildcard = MakePrincipal(tenantId: "tenant-a", roles: ["*:Consumer", "*:SchemaPublisher", "*:TenantAdmin"]);
        evaluator.GetEffectiveRoles(wildcard, "tenant-a").ShouldBeEmpty();
        evaluator.HasRole(wildcard, GatewayRole.Consumer, "tenant-a").ShouldBeFalse();
        evaluator.HasExactRole(wildcard, nameof(GatewayRole.Consumer), "tenant-a").ShouldBeFalse();
        evaluator.HasExactRole(wildcard, nameof(GatewayRole.TenantAdmin), "tenant-a").ShouldBeFalse();

        // Invalid tenant claim fails closed in GetEffectiveRoles and HasExactRole, even with ClusterAdmin or matching prefix.
        var invalidClaim = MakePrincipal(tenantId: "evil<script>tenant", roles: ["ClusterAdmin", "tenant-a:Consumer", "Consumer"]);
        evaluator.GetEffectiveRoles(invalidClaim, "tenant-a").ShouldBeEmpty();
        evaluator.HasRole(invalidClaim, GatewayRole.ClusterAdmin).ShouldBeFalse();
        evaluator.HasExactRole(invalidClaim, nameof(GatewayRole.ClusterAdmin)).ShouldBeFalse();
        evaluator.HasExactRole(invalidClaim, nameof(GatewayRole.Consumer), "tenant-a").ShouldBeFalse();

        // Cross-tenant tenant_id claim fails closed for non-ClusterAdmin roles in GetEffectiveRoles and HasExactRole.
        var crossTenant = MakePrincipal(tenantId: "tenant-a", roles: ["Consumer", "tenant-b:Consumer", "tenant-b:TenantAdmin"]);
        evaluator.GetEffectiveRoles(crossTenant, "tenant-b").ShouldBeEmpty();
        evaluator.HasRole(crossTenant, GatewayRole.Consumer, "tenant-b").ShouldBeFalse();
        evaluator.HasExactRole(crossTenant, nameof(GatewayRole.Consumer), "tenant-b").ShouldBeFalse();
        evaluator.HasExactRole(crossTenant, nameof(GatewayRole.TenantAdmin), "tenant-b").ShouldBeFalse();
        evaluator.GetEffectiveRoles(crossTenant, "tenant-a").ShouldBe([GatewayRole.Consumer]);

        // Canonical ClusterAdmin remains cross-tenant for global ClusterAdmin, never for foreign tenant-scoped product roles.
        var clusterCrossTenant = MakePrincipal(tenantId: "tenant-a", roles: ["ClusterAdmin", "tenant-b:DataProductAdmin"]);
        evaluator.GetEffectiveRoles(clusterCrossTenant, "tenant-b").ShouldBe([GatewayRole.ClusterAdmin]);
        evaluator.HasRole(clusterCrossTenant, GatewayRole.Consumer, "tenant-b").ShouldBeTrue();
        evaluator.HasExactRole(clusterCrossTenant, nameof(GatewayRole.DataProductAdmin), "tenant-b").ShouldBeFalse();
    }

    [Fact]
    public void SecurityPrincipalContext_HasRole_SupportsTenantPrefixedGovernanceAdmin_WhileKeepingClusterAdminGlobalOnly()
    {
        var matching = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-GOV"),
            ResolvedTenant = new TenantId("tenant-a"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tenant-a:GovernanceAdmin" },
            ClusterRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AuthenticationScheme = "Bearer"
        };

        matching.HasRole(GatewayRole.GovernanceAdmin).ShouldBeTrue();
        matching.HasRole(GatewayRole.SecurityAuditor).ShouldBeTrue();
        matching.HasRole(GatewayRole.SchemaPublisher).ShouldBeTrue();
        matching.HasRole(GatewayRole.Consumer).ShouldBeTrue();
        matching.HasRole(GatewayRole.ClusterAdmin).ShouldBeFalse();
        matching.HasRole(GatewayRole.TenantAdmin).ShouldBeFalse();

        var crossTenant = matching with
        {
            TenantRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tenant-b:GovernanceAdmin", "*:GovernanceAdmin", "tenant-a:ClusterAdmin" }
        };
        crossTenant.HasRole(GatewayRole.GovernanceAdmin).ShouldBeFalse();
        crossTenant.HasRole(GatewayRole.ClusterAdmin).ShouldBeFalse();

        var noTenant = matching with { ResolvedTenant = null };
        noTenant.HasRole(GatewayRole.GovernanceAdmin).ShouldBeFalse();
    }
}
