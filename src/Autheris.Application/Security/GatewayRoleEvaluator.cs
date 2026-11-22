namespace Autheris.Application.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Security;

/// <summary>
/// Canonical RBAC evaluator implementing hierarchy and tenant-scoped role checks.
/// </summary>
public sealed class GatewayRoleEvaluator(IIdentityRealmRegistry? realmRegistry = null) : IGatewayRoleEvaluator
{
    private readonly IIdentityRealmRegistry? _realmRegistry = realmRegistry;
    public bool HasRole(ClaimsPrincipal? principal, GatewayRole role, string? tenantId = null)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var effectiveRoles = GetEffectiveRoles(principal, tenantId);
        foreach (var effective in effectiveRoles)
        {
            if (effective.Implies(role))
            {
                return true;
            }
        }

        return false;
    }

    public bool HasRole(ClaimsPrincipal? principal, string roleName, string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(roleName) || principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (GatewayRoleExtensions.TryParseGlobalRole(roleName, out var parsedRole))
        {
            return HasRole(principal, parsedRole, tenantId);
        }

        return HasExactRole(principal, roleName, tenantId);
    }

    public bool HasAnyRole(ClaimsPrincipal? principal, IEnumerable<GatewayRole> roles, string? tenantId = null)
    {
        ArgumentNullException.ThrowIfNull(roles);

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var effectiveRoles = GetEffectiveRoles(principal, tenantId);
        foreach (var required in roles)
        {
            foreach (var effective in effectiveRoles)
            {
                if (effective.Implies(required))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool HasAnyRole(ClaimsPrincipal? principal, IEnumerable<string> roleNames, string? tenantId = null)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        foreach (var name in roleNames)
        {
            if (HasRole(principal, name, tenantId))
            {
                return true;
            }
        }

        return false;
    }

    public bool HasExactRole(ClaimsPrincipal? principal, string roleName, string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(roleName) || principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        bool isForwardAuth = IsForwardAuthPrincipal(principal);
        bool hasAllowed = TryGetAllowedGatewayRoles(principal, _realmRegistry, out var allowedRoles);

        if (GatewayRoleExtensions.TryParseGatewayRoleValue(roleName, out _, out var targetRole))
        {
            if (isForwardAuth && targetRole == GatewayRole.ClusterAdmin)
            {
                return false;
            }

            if (hasAllowed && !allowedRoles.Contains(targetRole))
            {
                return false;
            }
        }
        else if (hasAllowed)
        {
            return false;
        }

        var rawRoles = principal.GetUserRoles();
        foreach (var raw in rawRoles)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (isForwardAuth && ClusterAdminPolicy.IsClusterAdminRole(raw))
            {
                continue;
            }

            if (hasAllowed &&
                GatewayRoleExtensions.TryParseGatewayRoleValue(raw, out _, out var rawGatewayRole) &&
                !allowedRoles.Contains(rawGatewayRole))
            {
                continue;
            }

            var colonIdx = raw.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx > 0)
            {
                var roleTenant = raw[..colonIdx];
                var name = raw[(colonIdx + 1)..];

                if (!string.IsNullOrEmpty(tenantId) &&
                    (string.Equals(roleTenant, tenantId, StringComparison.OrdinalIgnoreCase) || roleTenant == "*") &&
                    string.Equals(name, roleName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else
            {
                if (string.Equals(raw, roleName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return principal.IsInRole(roleName);
    }

    public bool HasAnyExactRole(ClaimsPrincipal? principal, IEnumerable<string> roleNames, string? tenantId = null)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        foreach (var name in roleNames)
        {
            if (HasExactRole(principal, name, tenantId))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlySet<GatewayRole> GetEffectiveRoles(ClaimsPrincipal? principal, string? tenantId = null)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return new HashSet<GatewayRole>();
        }

        var claim = Autheris.Domain.Tenancy.TenantClaimReader.Read(principal);
        if (claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Invalid)
        {
            return new HashSet<GatewayRole>();
        }

        string? explicitTenant = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();
        bool hasClaimTenant = claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Valid;
        bool foreignTenantMismatch = hasClaimTenant &&
                                     explicitTenant is not null &&
                                     !string.Equals(claim.Tenant.Value, explicitTenant, StringComparison.OrdinalIgnoreCase);
        string? effectiveTenant = explicitTenant ?? (hasClaimTenant ? claim.Tenant.Value : null);

        var result = new HashSet<GatewayRole>();
        var rawRoles = principal.GetUserRoles();
        bool isForwardAuth = IsForwardAuthPrincipal(principal);
        bool hasAllowed = TryGetAllowedGatewayRoles(principal, _realmRegistry, out var allowedRoles);

        foreach (var raw in rawRoles)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (raw.Contains(':', StringComparison.Ordinal))
            {
                // Strict tenant-scoped form "tenant1:Consumer": exactly one colon, non-empty parts, exact role name.
                // Anything else containing ':' (":ClusterAdmin", "a:b:Consumer", "tenant:") is malformed and grants nothing.
                if (!GatewayRoleExtensions.TryParseTenantScopedRole(raw, out var roleTenant, out var parsedRole))
                {
                    continue;
                }

                // ClusterAdmin is strictly a global platform role and can never be granted via a tenant prefix.
                if (parsedRole == GatewayRole.ClusterAdmin)
                {
                    continue;
                }

                // Tenant-scoped-only and tenant-bound admin roles never match the wildcard '*' tenant.
                bool allowsWildcard = !parsedRole.IsTenantScopedOnly() && !IsTenantBoundAdminRole(parsedRole);
                if (foreignTenantMismatch ||
                    string.IsNullOrEmpty(effectiveTenant) ||
                    (!string.Equals(roleTenant, effectiveTenant, StringComparison.OrdinalIgnoreCase) && (!allowsWildcard || roleTenant != "*")))
                {
                    continue;
                }

                if (hasAllowed && !allowedRoles.Contains(parsedRole))
                {
                    continue;
                }

                result.Add(parsedRole);
            }
            else if (GatewayRoleExtensions.TryParseGlobalRole(raw, out var globalRole) && !globalRole.IsTenantScopedOnly())
            {
                if (isForwardAuth && globalRole == GatewayRole.ClusterAdmin)
                {
                    continue;
                }

                if (hasAllowed && !allowedRoles.Contains(globalRole))
                {
                    continue;
                }

                if (globalRole == GatewayRole.ClusterAdmin)
                {
                    result.Add(globalRole);
                    continue;
                }

                if (foreignTenantMismatch)
                {
                    continue;
                }

                if (IsTenantBoundAdminRole(globalRole))
                {
                    // Unprefixed GovernanceAdmin / TenantAdmin require a valid matching tenant claim.
                    if (!hasClaimTenant ||
                        string.IsNullOrEmpty(effectiveTenant) ||
                        !string.Equals(claim.Tenant.Value, effectiveTenant, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                result.Add(globalRole);
            }
        }

        return result;
    }

    private static bool IsTenantBoundAdminRole(GatewayRole role) =>
        role is GatewayRole.GovernanceAdmin or GatewayRole.TenantAdmin;

    /// <summary>
    /// Evaluates whether the principal holds <paramref name="roleName"/> unprefixed or scoped to the
    /// effective tenant (never matching wildcard prefixes, and failing closed on invalid or mismatched tenant claims).
    /// </summary>
    public static bool HasTenantBoundRole(ClaimsPrincipal? principal, string roleName, string? tenantId = null) =>
        HasTenantBoundRole(principal, roleName, tenantId, null);

    public static bool HasTenantBoundRole(
        ClaimsPrincipal? principal,
        string roleName,
        string? tenantId,
        IIdentityRealmRegistry? realmRegistry)
    {
        if (principal?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(roleName))
        {
            return false;
        }

        bool isForwardAuth = IsForwardAuthPrincipal(principal);
        bool hasAllowed = TryGetAllowedGatewayRoles(principal, realmRegistry, out var allowedRoles);
        if (GatewayRoleExtensions.TryParseGatewayRoleValue(roleName, out _, out var targetRole))
        {
            if (isForwardAuth && targetRole == GatewayRole.ClusterAdmin)
            {
                return false;
            }

            if (hasAllowed && !allowedRoles.Contains(targetRole))
            {
                return false;
            }
        }
        else if (hasAllowed)
        {
            return false;
        }

        var claim = Autheris.Domain.Tenancy.TenantClaimReader.Read(principal);
        if (claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Invalid)
        {
            return false;
        }

        string? effectiveTenant;
        if (claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Valid)
        {
            if (!string.IsNullOrWhiteSpace(tenantId) &&
                !string.Equals(claim.Tenant.Value, tenantId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            effectiveTenant = string.IsNullOrWhiteSpace(tenantId) ? claim.Tenant.Value : tenantId.Trim();
        }
        else
        {
            effectiveTenant = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();
        }

        foreach (var raw in principal.GetUserRoles())
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var trimmed = raw.Trim();
            if (isForwardAuth && ClusterAdminPolicy.IsClusterAdminRole(trimmed))
            {
                continue;
            }

            if (!trimmed.Contains(':', StringComparison.Ordinal))
            {
                if ((claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Valid || string.IsNullOrWhiteSpace(tenantId)) &&
                    string.Equals(trimmed, roleName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (string.IsNullOrEmpty(effectiveTenant))
            {
                continue;
            }

            var firstColon = trimmed.IndexOf(':', StringComparison.Ordinal);
            var lastColon = trimmed.LastIndexOf(':', StringComparison.Ordinal);
            if (firstColon <= 0 || firstColon != lastColon || firstColon == trimmed.Length - 1)
            {
                continue;
            }

            var roleTenant = trimmed[..firstColon];
            var parsedRoleName = trimmed[(firstColon + 1)..];
            if (roleTenant != "*" &&
                string.Equals(roleTenant, effectiveTenant, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parsedRoleName, roleName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsForwardAuthPrincipal(ClaimsPrincipal principal) =>
        principal.Identities.Any(i =>
            i.IsAuthenticated &&
            string.Equals(i.AuthenticationType, ClusterAdminPolicy.ForwardAuthScheme, StringComparison.OrdinalIgnoreCase));

    internal static bool TryGetAllowedGatewayRoles(
        ClaimsPrincipal principal,
        IIdentityRealmRegistry? realmRegistry,
        out HashSet<GatewayRole> allowedRoles)
    {
        bool hasClaim = RoleResolutionClaims.TryReadAllowedGatewayRoles(principal, out var fromClaim);
        bool hasRealm = false;
        IReadOnlySet<GatewayRole>? fromRealm = null;

        if (realmRegistry is not null)
        {
            var realmId = principal.FindFirst("autheris:realm")?.Value;
            if (!string.IsNullOrWhiteSpace(realmId))
            {
                hasRealm = true;
                fromRealm = realmRegistry.GetRealm(realmId)?.AllowedGatewayRoles;
            }
        }

        if (hasClaim && hasRealm)
        {
            allowedRoles = new HashSet<GatewayRole>(fromClaim);
            if (fromRealm is null)
            {
                allowedRoles.Clear();
            }
            else
            {
                allowedRoles.IntersectWith(fromRealm);
            }

            return true;
        }

        if (hasClaim)
        {
            allowedRoles = fromClaim;
            return true;
        }

        if (hasRealm)
        {
            allowedRoles = fromRealm is null ? [] : new HashSet<GatewayRole>(fromRealm);
            return true;
        }

        allowedRoles = [];
        return false;
    }
}
