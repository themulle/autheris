using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Tenancy;

namespace Autheris.Domain.Security;

public enum ConsentRevocationScope
{
    GranteeOrApproverOnly = 0,
    TenantGovernanceAdmin = 1,
    ClusterAdmin = 2
}

/// <summary>
/// Token-bound authorization context required by consent revocation and replacement entry points.
/// It is created either from the caller's token (<see cref="FromPrincipal"/>) or, for a revocation the grant revoke
/// authority already verified, through <see cref="ForVerifiedRevocation"/>. An architecture ratchet allows only the grant
/// revoke authority to call <see cref="ForVerifiedRevocation"/>, so no other code path can mint tenant-admin authority.
/// </summary>
public sealed class ConsentRevocationAuthority
{
    internal static readonly ConsentRevocationAuthority GranteeOrApproverInternal =
        new(ConsentRevocationScope.GranteeOrApproverOnly, null, denyAllTenants: true);

    private readonly bool _denyAllTenants;

    private ConsentRevocationAuthority(ConsentRevocationScope scope, TenantId? tenantId, bool denyAllTenants = false, string? verifiedCapacity = null)
    {
        Scope = scope;
        TenantId = tenantId;
        _denyAllTenants = denyAllTenants;
        VerifiedCapacity = verifiedCapacity;
    }

    public ConsentRevocationScope Scope { get; }

    public TenantId? TenantId { get; }

    /// <summary>
    /// The capacity the grant revoke authority verified (for example <c>ContainerOwner</c>); recorded in the revocation
    /// audit entry. <see langword="null"/> for token-derived authority.
    /// </summary>
    public string? VerifiedCapacity { get; }

    /// <summary>
    /// Authority for a revocation that the grant revoke authority already authorized for <paramref name="tenant"/>
    /// (grantee, container owner member, governance, or IT for grants it may decide). Only the grant revoke authority may
    /// call this (architecture ratchet); never derive it from caller input.
    /// </summary>
    /// <summary>The capacity recorded in the revocation audit entry for a revocation performed with this authority.</summary>
    public string DescribeCapacity(bool isAdminForTenant, bool isGranteeSelf) =>
        VerifiedCapacity
        ?? (isAdminForTenant ? Scope.ToString() : isGranteeSelf ? "Grantee" : "Approver");

    public static ConsentRevocationAuthority ForVerifiedRevocation(TenantId tenant, string capacity)
    {
        if (string.IsNullOrWhiteSpace(tenant.Value) || Autheris.Domain.Common.TenantId.IsReserved(tenant))
        {
            throw new ArgumentException("A verified revocation requires a concrete tenant.", nameof(tenant));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(capacity);
        return new(ConsentRevocationScope.TenantGovernanceAdmin, tenant, verifiedCapacity: capacity);
    }

    public static ConsentRevocationAuthority FromPrincipal(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return GranteeOrApproverInternal;
        }

        if (ClusterAdminPolicy.IsCanonicalClusterAdmin(principal))
        {
            return new ConsentRevocationAuthority(ConsentRevocationScope.ClusterAdmin, null);
        }

        var claimTenant = TenantClaimReader.Read(principal);
        if (claimTenant.Kind == ClaimTenantKind.Invalid)
        {
            return new ConsentRevocationAuthority(ConsentRevocationScope.GranteeOrApproverOnly, null, denyAllTenants: true);
        }

        var roles = principal.GetUserRoles();
        var hasUnprefixedGovernanceAdmin = false;
        var prefixedTenants = new HashSet<TenantId>();

        foreach (var raw in roles)
        {
            if (GatewayRoleExtensions.TryParseGlobalRole(raw, out var globalRole) &&
                globalRole == GatewayRole.GovernanceAdmin)
            {
                hasUnprefixedGovernanceAdmin = true;
            }
            else if (GatewayRoleExtensions.TryParseTenantScopedRole(raw, out var rawTenant, out var scopedRole) &&
                     scopedRole == GatewayRole.GovernanceAdmin &&
                     Autheris.Domain.Common.TenantId.TryParse(rawTenant, out var parsedRoleTenant))
            {
                prefixedTenants.Add(parsedRoleTenant);
            }
        }

        if (hasUnprefixedGovernanceAdmin && claimTenant.Kind == ClaimTenantKind.Valid)
        {
            return new ConsentRevocationAuthority(ConsentRevocationScope.TenantGovernanceAdmin, claimTenant.Tenant);
        }

        if (prefixedTenants.Count > 0)
        {
            if (claimTenant.Kind == ClaimTenantKind.Valid)
            {
                if (prefixedTenants.Contains(claimTenant.Tenant))
                {
                    return new ConsentRevocationAuthority(ConsentRevocationScope.TenantGovernanceAdmin, claimTenant.Tenant);
                }

                return new ConsentRevocationAuthority(ConsentRevocationScope.GranteeOrApproverOnly, null, denyAllTenants: true);
            }

            if (prefixedTenants.Count == 1)
            {
                return new ConsentRevocationAuthority(ConsentRevocationScope.TenantGovernanceAdmin, prefixedTenants.First());
            }

            return new ConsentRevocationAuthority(ConsentRevocationScope.GranteeOrApproverOnly, null, denyAllTenants: true);
        }

        if (claimTenant.Kind == ClaimTenantKind.Valid)
        {
            return new ConsentRevocationAuthority(ConsentRevocationScope.GranteeOrApproverOnly, claimTenant.Tenant);
        }

        return GranteeOrApproverInternal;
    }

    public bool CanAccessTenant(TenantId consentTenant)
    {
        if (_denyAllTenants)
        {
            return false;
        }

        return Scope switch
        {
            ConsentRevocationScope.ClusterAdmin => true,
            ConsentRevocationScope.TenantGovernanceAdmin => TenantId.HasValue && TenantId.Value == consentTenant,
            _ => TenantId.HasValue && TenantId.Value == consentTenant
        };
    }

    public bool AllowsAdminForTenant(TenantId consentTenant)
    {
        if (_denyAllTenants)
        {
            return false;
        }

        return Scope switch
        {
            ConsentRevocationScope.ClusterAdmin => true,
            ConsentRevocationScope.TenantGovernanceAdmin => TenantId.HasValue && TenantId.Value == consentTenant,
            _ => false
        };
    }

    public bool TryCheckTenantAccess(string? rawTenant, out bool isAdminForTenant, out TenantId auditTenant)
    {
        var hasValidTenant = Autheris.Domain.Common.TenantId.TryParse(rawTenant, out var parsedTenant);
        auditTenant = hasValidTenant ? parsedTenant : Autheris.Domain.Common.TenantId.Platform;
        if (_denyAllTenants || (hasValidTenant && !CanAccessTenant(parsedTenant)))
        {
            isAdminForTenant = false;
            return false;
        }

        isAdminForTenant = (!_denyAllTenants && Scope == ConsentRevocationScope.ClusterAdmin)
                           || (hasValidTenant && AllowsAdminForTenant(parsedTenant));
        return true;
    }
}
