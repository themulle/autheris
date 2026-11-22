namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class GovernanceCapabilitiesTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly ContainerRef DomainContainer = new(ContainerKind.Domain, Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static ClaimsPrincipal MakePrincipal(
        string scheme = "Bearer",
        string? tenantId = null,
        IEnumerable<GatewayRole>? allowedGatewayRoles = null,
        params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-TEST-USER")
        };

        if (!string.IsNullOrWhiteSpace(tenantId))
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

    private static SecurityPrincipalContext MakeContext(
        ClaimsPrincipal principal,
        TenantId resolvedTenant,
        bool tenantSelectedByHeader = false,
        GroupResolutionStatus groupStatus = GroupResolutionStatus.Complete) =>
        SecurityPrincipalContext.FromPrincipal(principal, overrideTenant: resolvedTenant) with
        {
            TenantSelectedByHeader = tenantSelectedByHeader,
            GroupResolutionStatus = groupStatus
        };

    [Fact]
    public async Task Tenant_scoped_role_only_for_its_tenant()
    {
        var capabilities = new GovernanceCapabilities();

        // Tenant-bound GovernanceAdmin (unprefixed with matching tenant claim)
        var govA = MakePrincipal(tenantId: TenantA.Value, roles: "GovernanceAdmin");
        capabilities.IsGovernance(govA, TenantA).ShouldBeTrue();
        capabilities.IsGovernance(govA, TenantB).ShouldBeFalse();
        capabilities.CanEditPolicy(govA, TenantA).ShouldBeTrue();
        capabilities.CanEditPolicy(govA, TenantB).ShouldBeFalse();
        (await capabilities.CanClassifyAsync(MakeContext(govA, TenantA), DomainContainer, TenantA, CancellationToken.None)).ShouldBeTrue();
        (await capabilities.CanClassifyAsync(MakeContext(govA, TenantA), DomainContainer, TenantB, CancellationToken.None)).ShouldBeFalse();

        // Explicitly prefixed tenant-a:GovernanceAdmin without tenant claim
        var prefixedGovA = MakePrincipal(tenantId: null, roles: "tenant-a:GovernanceAdmin");
        capabilities.IsGovernance(prefixedGovA, TenantA).ShouldBeTrue();
        capabilities.IsGovernance(prefixedGovA, TenantB).ShouldBeFalse();

        // Unprefixed GovernanceAdmin without any tenant claim has no tenant scope and must fail closed
        var untetheredGov = MakePrincipal(tenantId: null, roles: "GovernanceAdmin");
        capabilities.IsGovernance(untetheredGov, TenantA).ShouldBeFalse();
        capabilities.IsGovernance(untetheredGov, TenantB).ShouldBeFalse();

        // Wildcard *:GovernanceAdmin is always rejected
        var wildcardGov = MakePrincipal(tenantId: TenantA.Value, roles: "*:GovernanceAdmin");
        capabilities.IsGovernance(wildcardGov, TenantA).ShouldBeFalse();

        // TenantAdmin is IT admin only for its own tenant, never GovernanceAdmin
        var itA = MakePrincipal(tenantId: TenantA.Value, roles: "tenant-a:TenantAdmin");
        capabilities.IsItAdmin(itA, TenantA).ShouldBeTrue();
        capabilities.IsItAdmin(itA, TenantB).ShouldBeFalse();
        capabilities.IsGovernance(itA, TenantA).ShouldBeFalse();

        // ForwardAuth without explicit realm-allowed permission cannot escalate to GovernanceAdmin or TenantAdmin
        var forwardUnpermitted = MakePrincipal(
            scheme: ClusterAdminPolicy.ForwardAuthScheme,
            tenantId: TenantA.Value,
            roles: ["GovernanceAdmin", "TenantAdmin", "ClusterAdmin"]);
        capabilities.IsGovernance(forwardUnpermitted, TenantA).ShouldBeFalse();
        capabilities.IsItAdmin(forwardUnpermitted, TenantA).ShouldBeFalse();

        // ForwardAuth with explicit realm-allowed GovernanceAdmin succeeds for its tenant, while ClusterAdmin still fails
        var forwardPermitted = MakePrincipal(
            scheme: ClusterAdminPolicy.ForwardAuthScheme,
            tenantId: TenantA.Value,
            allowedGatewayRoles: [GatewayRole.GovernanceAdmin, GatewayRole.ClusterAdmin],
            roles: ["GovernanceAdmin", "ClusterAdmin"]);
        capabilities.IsGovernance(forwardPermitted, TenantA).ShouldBeTrue();
        capabilities.IsGovernance(forwardPermitted, TenantB).ShouldBeFalse();
        capabilities.IsItAdmin(forwardPermitted, TenantB).ShouldBeFalse();
    }

    [Fact]
    public async Task Tenant_taken_from_object_not_header()
    {
        var capabilities = new GovernanceCapabilities();

        // Caller is GovernanceAdmin in tenant-a, while header/context overrides ResolvedTenant to tenant-b
        var govA = MakePrincipal(tenantId: TenantA.Value, roles: "GovernanceAdmin");
        var headerOverriddenCtx = MakeContext(govA, resolvedTenant: TenantB, tenantSelectedByHeader: true);

        // Authorizing against objectTenant=tenant-b must fail even though ResolvedTenant (header) is tenant-b
        (await capabilities.CanClassifyAsync(headerOverriddenCtx, DomainContainer, TenantB, CancellationToken.None)).ShouldBeFalse();

        // Authorizing against objectTenant=tenant-a succeeds because objectTenant matches caller's governance tenant
        (await capabilities.CanClassifyAsync(headerOverriddenCtx, DomainContainer, TenantA, CancellationToken.None)).ShouldBeTrue();

        // Caller is GovernanceAdmin in tenant-b, while context ResolvedTenant is tenant-a
        var govB = MakePrincipal(tenantId: TenantB.Value, roles: "GovernanceAdmin");
        var spoofedCtx = MakeContext(govB, resolvedTenant: TenantA, tenantSelectedByHeader: true);
        (await capabilities.CanClassifyAsync(spoofedCtx, DomainContainer, TenantA, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task It_alone_cannot_classify()
    {
        var capabilities = new GovernanceCapabilities();

        // TenantAdmin is IT admin, never governance/classifier/policy editor
        var tenantAdmin = MakePrincipal(tenantId: TenantA.Value, roles: "TenantAdmin");
        var tenantAdminCtx = MakeContext(tenantAdmin, TenantA);
        capabilities.IsItAdmin(tenantAdmin, TenantA).ShouldBeTrue();
        capabilities.IsGovernance(tenantAdmin, TenantA).ShouldBeFalse();
        capabilities.CanEditPolicy(tenantAdmin, TenantA).ShouldBeFalse();
        (await capabilities.CanClassifyAsync(tenantAdminCtx, DomainContainer, TenantA, CancellationToken.None)).ShouldBeFalse();

        // Canonical ClusterAdmin is IT admin across tenants, NEVER governance/classifier/policy editor
        var clusterAdmin = MakePrincipal(tenantId: TenantA.Value, roles: "ClusterAdmin");
        var clusterAdminCtx = MakeContext(clusterAdmin, TenantA);
        capabilities.IsItAdmin(clusterAdmin, TenantA).ShouldBeTrue();
        capabilities.IsItAdmin(clusterAdmin, TenantB).ShouldBeTrue();
        capabilities.IsGovernance(clusterAdmin, TenantA).ShouldBeFalse();
        capabilities.CanEditPolicy(clusterAdmin, TenantA).ShouldBeFalse();
        (await capabilities.CanClassifyAsync(clusterAdminCtx, DomainContainer, TenantA, CancellationToken.None)).ShouldBeFalse();

        // IsOwnerMemberAsync stub is fail-closed (false for authenticated, degraded, and anonymous)
        var regularUser = MakePrincipal(tenantId: TenantA.Value, roles: "Consumer");
        (await capabilities.IsOwnerMemberAsync(MakeContext(regularUser, TenantA), DomainContainer, CancellationToken.None)).ShouldBeFalse();
        (await capabilities.IsOwnerMemberAsync(MakeContext(regularUser, TenantA, groupStatus: GroupResolutionStatus.Degraded), DomainContainer, CancellationToken.None)).ShouldBeFalse();
        (await capabilities.IsOwnerMemberAsync(SecurityPrincipalContext.Anonymous(), DomainContainer, CancellationToken.None)).ShouldBeFalse();
    }
}
