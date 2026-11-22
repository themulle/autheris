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
    public void GetEffectiveRoles_TenantBoundAdminRoles_ResolveFromClaimAndExplicitPrefix()
    {
        var evaluator = new GatewayRoleEvaluator();

        // Unprefixed GovernanceAdmin / TenantAdmin with valid tenant claim resolves when tenantId is null or matches.
        var unprefixedGov = MakePrincipal(tenantId: "tenant-a", roles: "GovernanceAdmin");
        evaluator.GetEffectiveRoles(unprefixedGov).ShouldBe([GatewayRole.GovernanceAdmin]);
        evaluator.GetEffectiveRoles(unprefixedGov, "tenant-a").ShouldBe([GatewayRole.GovernanceAdmin]);
        evaluator.GetEffectiveRoles(unprefixedGov, "tenant-b").ShouldBeEmpty();

        var unprefixedTenantAdmin = MakePrincipal(tenantId: "tenant-a", roles: "TenantAdmin");
        evaluator.GetEffectiveRoles(unprefixedTenantAdmin).ShouldBe([GatewayRole.TenantAdmin]);
        evaluator.GetEffectiveRoles(unprefixedTenantAdmin, "tenant-a").ShouldBe([GatewayRole.TenantAdmin]);
        evaluator.GetEffectiveRoles(unprefixedTenantAdmin, "tenant-b").ShouldBeEmpty();

        // Prefixed t:GovernanceAdmin / t:TenantAdmin resolves for matching claim or explicit tenant only.
        var prefixedGovWithClaim = MakePrincipal(tenantId: "tenant-a", roles: "tenant-a:GovernanceAdmin");
        evaluator.GetEffectiveRoles(prefixedGovWithClaim).ShouldBe([GatewayRole.GovernanceAdmin]);
        evaluator.GetEffectiveRoles(prefixedGovWithClaim, "tenant-a").ShouldBe([GatewayRole.GovernanceAdmin]);
        evaluator.GetEffectiveRoles(prefixedGovWithClaim, "tenant-b").ShouldBeEmpty();

        var prefixedGovNoClaim = MakePrincipal(tenantId: null, roles: "tenant-a:GovernanceAdmin");
        evaluator.GetEffectiveRoles(prefixedGovNoClaim).ShouldBeEmpty();
        evaluator.GetEffectiveRoles(prefixedGovNoClaim, "tenant-a").ShouldBe([GatewayRole.GovernanceAdmin]);
        evaluator.GetEffectiveRoles(prefixedGovNoClaim, "tenant-b").ShouldBeEmpty();
    }

    [Fact]
    public void GetEffectiveRoles_UntetheredAdminWithoutTenantClaim_FailsClosed()
    {
        var evaluator = new GatewayRoleEvaluator();

        var untetheredGov = MakePrincipal(tenantId: null, roles: "GovernanceAdmin");
        evaluator.GetEffectiveRoles(untetheredGov).ShouldBeEmpty();
        evaluator.GetEffectiveRoles(untetheredGov, "tenant-a").ShouldBeEmpty();

        var untetheredTenantAdmin = MakePrincipal(tenantId: null, roles: "TenantAdmin");
        evaluator.GetEffectiveRoles(untetheredTenantAdmin).ShouldBeEmpty();
        evaluator.GetEffectiveRoles(untetheredTenantAdmin, "tenant-a").ShouldBeEmpty();
    }

    [Fact]
    public void GovernanceAdmin_WithoutTenantClaim_Denied_EvenWhenTargetTenantProvided()
    {
        var evaluator = new GatewayRoleEvaluator();
        var untetheredGov = MakePrincipal(tenantId: null, roles: "GovernanceAdmin");

        evaluator.GetEffectiveRoles(untetheredGov, "tenant-a").ShouldBeEmpty();
        evaluator.HasRole(untetheredGov, GatewayRole.GovernanceAdmin, "tenant-a").ShouldBeFalse();
        evaluator.HasRole(untetheredGov, GatewayRole.GovernanceAdmin, null).ShouldBeFalse();
        GatewayRoleEvaluator.HasTenantBoundRole(untetheredGov, nameof(GatewayRole.GovernanceAdmin), "tenant-a").ShouldBeFalse();
        GatewayPolicies.HasRole(untetheredGov, GatewayRole.GovernanceAdmin, "tenant-a").ShouldBeFalse();
    }

    [Fact]
    public void TenantAdmin_WithoutTenantClaim_Denied_EvenWhenTargetTenantProvided()
    {
        var evaluator = new GatewayRoleEvaluator();
        var untetheredTenantAdmin = MakePrincipal(tenantId: null, roles: "TenantAdmin");

        evaluator.GetEffectiveRoles(untetheredTenantAdmin, "tenant-a").ShouldBeEmpty();
        evaluator.HasRole(untetheredTenantAdmin, GatewayRole.TenantAdmin, "tenant-a").ShouldBeFalse();
        evaluator.HasRole(untetheredTenantAdmin, GatewayRole.TenantAdmin, null).ShouldBeFalse();
        GatewayRoleEvaluator.HasTenantBoundRole(untetheredTenantAdmin, nameof(GatewayRole.TenantAdmin), "tenant-a").ShouldBeFalse();
        GatewayPolicies.HasRole(untetheredTenantAdmin, GatewayRole.TenantAdmin, "tenant-a").ShouldBeFalse();
    }

    [Theory]
    [InlineData("*:GovernanceAdmin")]
    [InlineData("*:TenantAdmin")]
    [InlineData("*:DataProductAdmin")]
    [InlineData("*:DataProductSync")]
    [InlineData("*:ClusterAdmin")]
    [InlineData("tenant-a:ClusterAdmin")]
    [InlineData("DataProductAdmin")]
    [InlineData("DataProductSync")]
    public void GetEffectiveRoles_WildcardTenantBoundAndUnprefixedProductAndPrefixedCluster_AreRejected(string role)
    {
        var evaluator = new GatewayRoleEvaluator();
        var principal = MakePrincipal(tenantId: "tenant-a", roles: role);

        evaluator.GetEffectiveRoles(principal).ShouldBeEmpty();
        evaluator.GetEffectiveRoles(principal, "tenant-a").ShouldBeEmpty();
    }

    [Fact]
    public void GetEffectiveRoles_DataProductRoles_RemainPrefixedOnly()
    {
        var evaluator = new GatewayRoleEvaluator();

        var prefixedProduct = MakePrincipal(tenantId: "tenant-a", roles: ["tenant-a:DataProductAdmin", "tenant-a:DataProductSync"]);
        evaluator.GetEffectiveRoles(prefixedProduct).ShouldBe([GatewayRole.DataProductAdmin, GatewayRole.DataProductSync]);
        evaluator.GetEffectiveRoles(prefixedProduct, "tenant-a").ShouldBe([GatewayRole.DataProductAdmin, GatewayRole.DataProductSync]);
        evaluator.GetEffectiveRoles(prefixedProduct, "tenant-b").ShouldBeEmpty();
    }

    [Fact]
    public void GetEffectiveRoles_InvalidTenantClaim_FailsClosed_EvenForClusterAdmin()
    {
        var evaluator = new GatewayRoleEvaluator();
        var invalidTenantAdmin = MakePrincipal(tenantId: "evil<script>tenant", roles: ["ClusterAdmin", "GovernanceAdmin", "Consumer"]);

        evaluator.GetEffectiveRoles(invalidTenantAdmin).ShouldBeEmpty();
        evaluator.GetEffectiveRoles(invalidTenantAdmin, "tenant-a").ShouldBeEmpty();
    }

    [Fact]
    public void GetEffectiveRoles_ForeignTenantMismatch_BlocksNonClusterRoles_AllowsCanonicalClusterAdmin()
    {
        var evaluator = new GatewayRoleEvaluator();
        var principal = MakePrincipal(tenantId: "tenant-a", roles: ["ClusterAdmin", "GovernanceAdmin", "SchemaPublisher"]);

        var tenantB = evaluator.GetEffectiveRoles(principal, "tenant-b");
        tenantB.ShouldBe([GatewayRole.ClusterAdmin]);
        evaluator.HasRole(principal, GatewayRole.GovernanceAdmin, "tenant-b").ShouldBeFalse();
        evaluator.HasRole(principal, GatewayRole.SchemaPublisher, "tenant-b").ShouldBeTrue(); // implied by ClusterAdmin

        var nonCluster = MakePrincipal(tenantId: "tenant-a", roles: ["GovernanceAdmin", "SchemaPublisher"]);
        evaluator.GetEffectiveRoles(nonCluster, "tenant-b").ShouldBeEmpty();
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
        evaluator.HasRole(processed, GatewayRole.GovernanceAdmin, "other-tenant").ShouldBeFalse();
    }
#endif
}
