namespace Autheris.Application.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Security;

/// <summary>
/// K-K10: Canonical RBAC evaluator implementing hierarchy and tenant-scoped role checks.
/// </summary>
public sealed class GatewayRoleEvaluator : IGatewayRoleEvaluator
{
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

        var rawRoles = principal.GetUserRoles();
        foreach (var raw in rawRoles)
        {
            if (string.IsNullOrWhiteSpace(raw))
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

        var result = new HashSet<GatewayRole>();
        var rawRoles = principal.GetUserRoles();

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

                // Security Guard: Tenant-scoped roles only apply when evaluating within that specific tenant.
                // They never satisfy a global (tenantId == null) authorization check unless the role is wildcard (*).
                if (string.IsNullOrEmpty(tenantId) ||
                    (!string.Equals(roleTenant, tenantId, StringComparison.OrdinalIgnoreCase) && roleTenant != "*"))
                {
                    continue;
                }

                // Security Guard: ClusterAdmin and GovernanceAdmin are strictly global platform roles.
                // They can never be granted via a tenant-scoped prefix (e.g. "tenant-1:ClusterAdmin").
                if (parsedRole is GatewayRole.ClusterAdmin or GatewayRole.GovernanceAdmin)
                {
                    continue;
                }

                // Data product roles are bound to one named tenant; the wildcard tenant never confers them.
                if (parsedRole.IsTenantScopedOnly() && roleTenant == "*")
                {
                    continue;
                }

                result.Add(parsedRole);
            }
            else if (GatewayRoleExtensions.TryParseGlobalRole(raw, out var globalRole) && !globalRole.IsTenantScopedOnly())
            {
                // Global role (applies across all tenants)
                result.Add(globalRole);
            }
        }

        return result;
    }

    /// <summary>
    /// Evaluates whether the principal holds <paramref name="roleName"/> unprefixed or scoped to the
    /// effective tenant (never matching wildcard prefixes, and failing closed on invalid or mismatched tenant claims).
    /// </summary>
    public static bool HasTenantBoundRole(ClaimsPrincipal? principal, string roleName, string? tenantId = null)
    {
        if (principal?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(roleName))
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
            if (!trimmed.Contains(':', StringComparison.Ordinal))
            {
                if (string.Equals(trimmed, roleName, StringComparison.OrdinalIgnoreCase))
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
}
