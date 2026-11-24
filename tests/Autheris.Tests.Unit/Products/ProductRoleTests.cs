using System.Security.Claims;
using Autheris.Application.Products;
using Autheris.Application.Security;
using Autheris.Domain.Governance;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

[Trait("Category", "Security")]
public sealed class ProductRoleTests
{
    private static readonly GatewayRole[] ProductRoles = [GatewayRole.DataProductAdmin, GatewayRole.DataProductSync];

    private static ClaimsPrincipal PrincipalWith(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)), "Test"));

    private static SecurityPrincipalContext Context(IEnumerable<string> tenantRoles, IEnumerable<string> clusterRoles) => new()
    {
        UserSid = new Sid("S-1-5-21-1"),
        TenantId = new TenantId("tenant-a"),
        GroupSids = new HashSet<Sid>(),
        TenantRoles = new HashSet<string>(tenantRoles),
        ClusterRoles = new HashSet<string>(clusterRoles),
        AuthenticationScheme = "Test"
    };

    [Fact]
    public void DataProductAdmin_not_implied_by_ClusterAdmin_TenantAdmin_GovernanceAdmin()
    {
        foreach (var product in ProductRoles)
        {
            foreach (var granted in Enum.GetValues<GatewayRole>().Where(r => r != product))
            {
                granted.Implies(product).ShouldBeFalse($"{granted} must not imply {product}");
            }

            product.Implies(product).ShouldBeTrue();
        }

        GatewayRole.ClusterAdmin.Implies(GatewayRole.TenantAdmin).ShouldBeTrue("the exception must not weaken the other implications");
        GatewayRole.DataProductAdmin.Implies(GatewayRole.Consumer).ShouldBeFalse();
    }

    [Fact]
    public void DataProductAdmin_tenant_scoped_only()
    {
        var evaluator = new GatewayRoleEvaluator();

        evaluator.GetEffectiveRoles(PrincipalWith("DataProductAdmin"), "tenant-a").ShouldBeEmpty();
        evaluator.GetEffectiveRoles(PrincipalWith("*:DataProductAdmin"), "tenant-a").ShouldBeEmpty();
        evaluator.GetEffectiveRoles(PrincipalWith("tenant-b:DataProductAdmin"), "tenant-a").ShouldBeEmpty();
        evaluator.GetEffectiveRoles(PrincipalWith("tenant-a:DataProductAdmin"), null).ShouldBeEmpty();
        evaluator.GetEffectiveRoles(PrincipalWith("tenant-a:DataProductAdmin"), "tenant-a").ShouldContain(GatewayRole.DataProductAdmin);
        evaluator.HasRole(PrincipalWith("ClusterAdmin", "GovernanceAdmin"), GatewayRole.DataProductAdmin, "tenant-a").ShouldBeFalse();
        evaluator.HasRole(PrincipalWith("tenant-a:DataProductSync"), GatewayRole.DataProductSync, "tenant-a").ShouldBeTrue();
        GatewayRoleExtensions.TryParseTenantScopedRole("tenant-a:DataProductAdmin", out var tenant, out var role).ShouldBeTrue();
        (tenant, role).ShouldBe(("tenant-a", GatewayRole.DataProductAdmin));
    }

    [Fact]
    public void DataProductAdmin_not_conferred_to_the_request_context_by_cluster_or_global_roles()
    {
        Context([], ["ClusterAdmin"]).HasRole(GatewayRole.DataProductAdmin).ShouldBeFalse();
        Context([], ["ClusterAdmin", "GovernanceAdmin", "DataProductAdmin"]).HasRole(GatewayRole.DataProductSync).ShouldBeFalse();
        Context(["TenantAdmin"], []).HasRole(GatewayRole.DataProductAdmin).ShouldBeFalse();
        Context(["DataProductAdmin"], []).HasRole(GatewayRole.DataProductAdmin).ShouldBeTrue();
        Context(["DataProductAdmin"], []).HasRole(GatewayRole.DataProductSync).ShouldBeFalse();
    }

    [Fact]
    public void DataProduct_admin_right_is_never_conferred_by_a_mapping()
    {
        FunctionalRight.DataProductAdmin.IsPrivileged.ShouldBeTrue();
        FunctionalRight.PrivilegedValues.ShouldContain("DataProduct.Admin");

        // Single approval (Amendment 2): a saved mapping is approved, but a privileged right is a capacity no mapping confers.
        Autheris.Application.Governance.Rights.Validators.FunctionalRightMappingValidator.ConfersCapacity(FunctionalRight.DataProductAdmin).ShouldBeTrue();
        var mapping = new FunctionalRightMapping(
            new TenantId("tenant-a"), "realm", "product-admins", FunctionalRight.DataProductAdmin, "alice",
            DateTimeOffset.UnixEpoch, "standard", "alice", DateTimeOffset.UnixEpoch);
        Should.Throw<System.Security.SecurityException>(() =>
            Autheris.Application.Governance.Rights.Validators.FunctionalRightMappingValidator.ValidateCreation(mapping, realm: null));
    }

    [Fact]
    public void Sync_role_limited_to_sync_endpoints()
    {
        var sid = new Sid("S-1-5-21-9002");
        var tenant = new TenantId("tenant-a");

        ProductAuthorityService.ForRestRoute(sid, tenant, isDataProductAdmin: false, isDataProductSync: true).ShouldBeNull();
        ProductAuthorityService.ForRestRoute(sid, tenant, isDataProductAdmin: true, isDataProductSync: true).ShouldBeNull();
        ProductAuthorityService.ForRestRoute(sid, tenant, isDataProductAdmin: true, isDataProductSync: false)!.ViaSyncPath.ShouldBeFalse();
    }
}
