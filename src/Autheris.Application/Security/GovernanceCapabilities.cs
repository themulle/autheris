namespace Autheris.Application.Security;

using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Identity;
using Autheris.Application.Products;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;

/// <summary>
/// Enforces authoritative capability boundaries separating governance authority,
/// IT administration, and container ownership scope.
/// </summary>
public sealed class GovernanceCapabilities(
    IIdentityRealmRegistry? realmRegistry = null,
    IGovernanceDomainRepository? domainRepository = null,
    IDataProductRepository? productRepository = null) : IGovernanceCapabilities
{
    private readonly IIdentityRealmRegistry? _realmRegistry = realmRegistry;
    private readonly IGovernanceDomainRepository? _domainRepository = domainRepository;
    private readonly IDataProductRepository? _productRepository = productRepository;

    public bool IsGovernance(ClaimsPrincipal? principal, TenantId tenant) =>
        HasTenantBoundAdminRole(principal, GatewayRole.GovernanceAdmin, tenant);

    public bool IsItAdmin(ClaimsPrincipal? principal, TenantId tenant)
    {
        if (principal?.Identity?.IsAuthenticated != true ||
            string.IsNullOrWhiteSpace(tenant.Value) ||
            TenantId.IsReserved(tenant))
        {
            return false;
        }

        var tenantClaim = TenantClaimReader.Read(principal);
        if (tenantClaim.Kind == ClaimTenantKind.Invalid)
        {
            return false;
        }

        if (ClusterAdminPolicy.IsCanonicalClusterAdmin(principal) &&
            IsRealmRolePermitted(principal, GatewayRole.ClusterAdmin, requireExplicitWhenForwardAuth: true))
        {
            return true;
        }

        return HasTenantBoundAdminRole(principal, GatewayRole.TenantAdmin, tenant);
    }

    public ValueTask<bool> IsOwnerMemberAsync(
        SecurityPrincipalContext principal,
        ContainerRef container,
        CancellationToken ct)
    {
        if (principal is null ||
            !principal.IsAuthenticated ||
            principal.TenantClaimInvalid ||
            principal.GroupResolutionStatus == GroupResolutionStatus.Degraded ||
            principal.ResolvedTenant is not { } tenant ||
            string.IsNullOrWhiteSpace(tenant.Value) ||
            TenantId.IsReserved(tenant))
        {
            return ValueTask.FromResult(false);
        }

        if (!TenantMatchesPrincipalClaim(principal, tenant))
        {
            return ValueTask.FromResult(false);
        }

        return IsOwnerMemberCoreAsync(principal, container, tenant, ct);
    }

    public async ValueTask<bool> CanClassifyAsync(
        SecurityPrincipalContext principal,
        ContainerRef container,
        TenantId objectTenant,
        CancellationToken ct)
    {
        if (principal is null ||
            !principal.IsAuthenticated ||
            principal.TenantClaimInvalid ||
            string.IsNullOrWhiteSpace(objectTenant.Value) ||
            TenantId.IsReserved(objectTenant))
        {
            return false;
        }

        if (IsGovernance(principal.Principal, objectTenant))
        {
            return true;
        }

        if (principal.GroupResolutionStatus == GroupResolutionStatus.Degraded ||
            !TenantMatchesPrincipalClaim(principal, objectTenant))
        {
            return false;
        }

        return await IsOwnerMemberCoreAsync(principal, container, objectTenant, ct).ConfigureAwait(false);
    }

    public bool CanEditPolicy(ClaimsPrincipal? principal, TenantId objectTenant) =>
        IsGovernance(principal, objectTenant);

    private async ValueTask<bool> IsOwnerMemberCoreAsync(
        SecurityPrincipalContext principal,
        ContainerRef container,
        TenantId objectTenant,
        CancellationToken ct)
    {
        if (container.Id == Guid.Empty || principal.GroupSids is null || principal.GroupSids.Count == 0)
        {
            return false;
        }

        Sid? ownerGroupSid = container.Kind switch
        {
            ContainerKind.Domain when _domainRepository is not null =>
                (await _domainRepository.GetAsync(objectTenant, container.Id, ct).ConfigureAwait(false))?.OwnerGroupSid,
            ContainerKind.DataProduct when _productRepository is not null =>
                await ResolveProductOwnerGroupSidAsync(objectTenant, container.Id, ct).ConfigureAwait(false),
            _ => null
        };

        return ownerGroupSid is { } sid &&
               !string.IsNullOrWhiteSpace(sid.Value) &&
               principal.GroupSids.Contains(sid);
    }

    private async ValueTask<Sid?> ResolveProductOwnerGroupSidAsync(TenantId tenant, Guid productId, CancellationToken ct)
    {
        if (_productRepository is null || productId == Guid.Empty)
        {
            return null;
        }

        var product = await _productRepository.GetProductByIdAsync(tenant, productId, ct).ConfigureAwait(false);
        return product?.OwnerGroupSid;
    }

    private static bool TenantMatchesPrincipalClaim(SecurityPrincipalContext principal, TenantId objectTenant)
    {
        if (principal.Principal is null)
        {
            return false;
        }

        var tenantClaim = TenantClaimReader.Read(principal.Principal);
        if (tenantClaim.Kind == ClaimTenantKind.Invalid)
        {
            return false;
        }

        if (tenantClaim.Kind == ClaimTenantKind.Valid)
        {
            return string.Equals(tenantClaim.Tenant.Value, objectTenant.Value, StringComparison.OrdinalIgnoreCase);
        }

        if (principal.TenantSelectedByHeader)
        {
            return false;
        }

        return principal.ResolvedTenant is { } resolved &&
               string.Equals(resolved.Value, objectTenant.Value, StringComparison.OrdinalIgnoreCase);
    }

    private bool HasTenantBoundAdminRole(ClaimsPrincipal? principal, GatewayRole targetRole, TenantId tenant)
    {
        if (principal?.Identity?.IsAuthenticated != true ||
            string.IsNullOrWhiteSpace(tenant.Value) ||
            TenantId.IsReserved(tenant))
        {
            return false;
        }

        var tenantClaim = TenantClaimReader.Read(principal);
        if (tenantClaim.Kind == ClaimTenantKind.Invalid)
        {
            return false;
        }

        if (tenantClaim.Kind == ClaimTenantKind.Valid &&
            !string.Equals(tenantClaim.Tenant.Value, tenant.Value, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsRealmRolePermitted(principal, targetRole, requireExplicitWhenForwardAuth: true))
        {
            return false;
        }

        var roleName = targetRole.ToString();
        if (tenantClaim.Kind == ClaimTenantKind.Valid)
        {
            return GatewayRoleEvaluator.HasTenantBoundRole(principal, roleName, tenant.Value, _realmRegistry);
        }

        return HasExplicitTenantScopedRoleClaim(principal, tenant.Value, targetRole);
    }

    private bool IsRealmRolePermitted(ClaimsPrincipal principal, GatewayRole targetRole, bool requireExplicitWhenForwardAuth)
    {
        bool isForwardAuth = GatewayRoleEvaluator.IsForwardAuthPrincipal(principal);
        if (isForwardAuth && targetRole == GatewayRole.ClusterAdmin)
        {
            return false;
        }

        if (GatewayRoleEvaluator.TryGetAllowedGatewayRoles(principal, _realmRegistry, out var allowedRoles))
        {
            return allowedRoles.Contains(targetRole);
        }

        return !requireExplicitWhenForwardAuth || !isForwardAuth;
    }

    private static bool HasExplicitTenantScopedRoleClaim(ClaimsPrincipal principal, string tenantId, GatewayRole targetRole)
    {
        foreach (var raw in principal.GetUserRoles())
        {
            if (GatewayRoleExtensions.TryParseGatewayRoleValue(raw, out var roleTenant, out var parsedRole) &&
                roleTenant is not null &&
                roleTenant != "*" &&
                string.Equals(roleTenant, tenantId, StringComparison.OrdinalIgnoreCase) &&
                parsedRole == targetRole)
            {
                return true;
            }
        }

        return false;
    }
}
