namespace Autheris.Application.Catalog.Services;

using System;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Application.Tenancy;
using Autheris.Domain.Tenancy;
using Autheris.Domain.Security;

/// <summary>Caller class of a catalog request, decided by <see cref="CallerScopeResolver"/>.</summary>
public enum CallerScope
{
    /// <summary>No usable principal (missing, unauthenticated, no SID, unresolved or invalid tenant): nothing is visible.</summary>
    None = 0,

    /// <summary>Canonical ClusterAdmin: whole catalog, all tenants.</summary>
    Global,

    /// <summary>
    /// GovernanceAdmin of the default tenant, the catalog owner (DT-A-7): the whole physical catalog metadata. Never widens a
    /// tenant-scoped object (virtual tables, consents, ReBAC, audit, lineage, subscriptions, jobs, access profiles; DT-SC-08).
    /// </summary>
    CatalogOwner,

    /// <summary>GovernanceAdmin of any other tenant: catalog confined to that tenant.</summary>
    Tenant,

    /// <summary>Authenticated caller with a resolved tenant and no administrator class: consent path only.</summary>
    Subject
}

/// <summary>
/// The only place that decides the caller class of every catalog channel (REST service, MCP tools and resources,
/// <c>invoke_api</c>, GraphQL). The decision comes from the authenticated <see cref="ClaimsPrincipal"/> and the tenant resolution only; it never reads
/// <c>RequestContext.Roles</c> or <c>CallerSecurityContext.IsClusterAdmin</c> (string-matched role lists).
/// </summary>
public static class CallerScopeResolver
{
    private const string GovernanceAdminRole = "GovernanceAdmin";

    /// <summary>
    /// Scope from an explicit <paramref name="resolution"/>. The catalog owner is the default tenant: a GovernanceAdmin whose resolved tenant
    /// <paramref name="catalogOwner"/> (<c>ITenantResolver.DefaultTenant</c>) is <see cref="CallerScope.CatalogOwner"/>.
    /// </summary>
    public static CallerScope Resolve(ClaimsPrincipal? user, TenantResolution resolution, TenantId? catalogOwner)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return CallerScope.None;
        }

        // SEC-CAT138-14: the no-SID rule lives here only. A principal without a SID (a name-only synthetic identity) is never a catalog
        // caller of any class; every real authentication path (cert, ForwardAuth, Basic, API key, JWT) carries one.
        if (user.GetUserSid() == null)
        {
            return CallerScope.None;
        }

        // An invalid tenant claim denies every caller class, ClusterAdmin included (DT-SC-02); an absent claim is not invalid.
        if (resolution.Outcome == TenantResolutionOutcome.Invalid)
        {
            return CallerScope.None;
        }

        // Excludes forward-auth identities and tenant-prefixed roles.
        if (ClusterAdminPolicy.IsCanonicalClusterAdmin(user))
        {
            return CallerScope.Global;
        }

        // Unresolved (no claim, no default) and invalid never map to a wider class; there is no claim-less consent path any more.
        if (resolution.Outcome is not (TenantResolutionOutcome.FromClaim or TenantResolutionOutcome.FromRealm or TenantResolutionOutcome.DefaultAssigned) ||
            resolution.Tenant is not { } tenant || tenant.Value is null)
        {
            return CallerScope.None;
        }

        if (!user.GetUserRoles().Contains(GovernanceAdminRole))
        {
            return CallerScope.Subject;
        }

        return IsOwner(tenant, catalogOwner) ? CallerScope.CatalogOwner : CallerScope.Tenant;
    }

    /// <summary>
    /// Scope for a caller whose tenant is read from the claims and filled with <paramref name="catalogOwner"/> (the default tenant) when absent.
    /// </summary>
    public static CallerScope Resolve(ClaimsPrincipal? user, TenantId? catalogOwner) =>
        Resolve(user, ResolutionOf(user, catalogOwner), catalogOwner);

    /// <summary>The tenant resolution of <paramref name="user"/> against <paramref name="catalogOwner"/> (the default tenant, <c>null</c> = none).</summary>
    public static TenantResolution ResolutionOf(ClaimsPrincipal? user, TenantId? catalogOwner) =>
        user?.Identity?.IsAuthenticated != true
            ? new TenantResolution(TenantResolutionOutcome.Unauthenticated, null)
            : TenantResolver.Decide(TenantClaimReader.Read(user), catalogOwner);

    /// <summary>True when <paramref name="tenant"/> is the catalog owner (the default tenant).</summary>
    public static bool IsOwner(TenantId tenant, TenantId? catalogOwner) =>
        catalogOwner is { } owner && owner.Value is not null && tenant.Value is not null &&
        string.Equals(owner.Value, tenant.Value, StringComparison.Ordinal);

    /// <summary>True when the caller class may list administrator metadata such as data sources.</summary>
    public static bool IsAdminView(CallerScope scope) => scope is CallerScope.Global or CallerScope.CatalogOwner or CallerScope.Tenant;

    /// <summary>True when the principal holds the GovernanceAdmin role (used for the diagnostic of administrators without a resolvable tenant).</summary>
    public static bool HoldsGovernanceAdminRole(ClaimsPrincipal? user) => user?.GetUserRoles().Contains(GovernanceAdminRole) == true;

    /// <summary>True when the principal carries an invalid, reserved or conflicting tenant claim (resolution outcome Invalid).</summary>
    public static bool HasInvalidTenantClaim(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true && TenantClaimReader.Read(user).Kind == ClaimTenantKind.Invalid;

    /// <summary>
    /// The 403 text for a data-source listing: a GovernanceAdmin without standing is told why. An invalid claim names the code
    /// <c>TENANT_CLAIM_INVALID</c> (never the value), a missing claim names the option.
    /// </summary>
    public static string DatasourceListingDenial(ClaimsPrincipal? user, CallerScope scope)
    {
        if (scope != CallerScope.None || user?.Identity?.IsAuthenticated != true || !HoldsGovernanceAdminRole(user))
        {
            return "Listing data sources requires a governance administrator.";
        }

        return HasInvalidTenantClaim(user)
            ? "Listing data sources is denied: the tenant claim of the caller is invalid (TENANT_CLAIM_INVALID)."
            : "Listing data sources requires an administrator with a tenant claim; single-tenant deployments must set Gateway:Tenancy:DefaultTenantId.";
    }
}
