namespace Autheris.Application.Catalog.Services;

using System;
using System.Security;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Domain.Security;

/// <summary>Caller class of a catalog request, decided by <see cref="CallerScopeResolver"/>.</summary>
public enum CallerScope
{
    /// <summary>No usable principal (missing, unauthenticated, no SID, invalid tenant claim): nothing is visible.</summary>
    None = 0,

    /// <summary>Canonical ClusterAdmin: whole catalog, all tenants.</summary>
    Global,

    /// <summary>GovernanceAdmin of a single-tenant deployment without tenant claim: whole catalog.</summary>
    Unrestricted,

    /// <summary>GovernanceAdmin with a real tenant claim: catalog confined to that tenant.</summary>
    Tenant,

    /// <summary>Authenticated caller with a tenant claim and no administrator class: consent path only.</summary>
    Subject,

    /// <summary>Multi-tenant deployment, principal without tenant claim: consent path only, never an administrator view.</summary>
    Claimless
}

/// <summary>
/// The only place that decides the caller class of every catalog channel (REST service, MCP tools and resources,
/// <c>invoke_api</c>, GraphQL). The decision comes from the authenticated <see cref="ClaimsPrincipal"/> only; it never reads
/// <c>RequestContext.Roles</c> or <c>CallerSecurityContext.IsClusterAdmin</c> (string-matched role lists).
/// </summary>
public static class CallerScopeResolver
{
    private const string GovernanceAdminRole = "GovernanceAdmin";

    public static CallerScope Resolve(ClaimsPrincipal? user, TenancyMode mode)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return CallerScope.None;
        }

        var sid = user.GetUserSid();
        if (sid == null && string.IsNullOrWhiteSpace(user.Identity?.Name))
        {
            return CallerScope.None;
        }

        // Excludes forward-auth identities and tenant-prefixed roles.
        if (ClusterAdminPolicy.IsCanonicalClusterAdmin(user))
        {
            return CallerScope.Global;
        }

        TenantId tenant;
        try
        {
            tenant = user.GetTenantId();
        }
        catch (SecurityException)
        {
            // An invalid tenant claim never maps to a wider class.
            return CallerScope.None;
        }

        var hasTenantClaim = tenant != TenantId.LegacySingleTenant;
        var isGovernanceAdmin = user.GetUserRoles().Contains(GovernanceAdminRole);

        if (mode == TenancyMode.Single)
        {
            if (!isGovernanceAdmin)
            {
                return CallerScope.Subject;
            }

            return hasTenantClaim ? CallerScope.Tenant : CallerScope.Unrestricted;
        }

        if (!hasTenantClaim)
        {
            return CallerScope.Claimless;
        }

        return isGovernanceAdmin ? CallerScope.Tenant : CallerScope.Subject;
    }

    /// <summary>True when the caller class may list administrator metadata such as data sources.</summary>
    public static bool IsAdminView(CallerScope scope) => scope is CallerScope.Global or CallerScope.Unrestricted or CallerScope.Tenant;

    /// <summary>True when the principal holds the GovernanceAdmin role (used for the downgrade diagnostic of claim-less administrators).</summary>
    public static bool HoldsGovernanceAdminRole(ClaimsPrincipal? user) => user?.GetUserRoles().Contains(GovernanceAdminRole) == true;
}
