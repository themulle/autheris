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
}
