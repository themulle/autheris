namespace Autheris.Application.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Application.Interfaces;
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

        if (GatewayRoleExtensions.TryParseRole(roleName, out var parsedRole))
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

            var colonIdx = raw.IndexOf(':');
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

            // Check if role is tenant-prefixed: "tenant1:DataOwner"
            var colonIdx = raw.IndexOf(':');
            if (colonIdx > 0)
            {
                var roleTenant = raw[..colonIdx];
                var roleName = raw[(colonIdx + 1)..];

                // Security Guard: Tenant-scoped roles only apply when evaluating within that specific tenant.
                // They never satisfy a global (tenantId == null) authorization check unless the role is wildcard (*).
                if (string.IsNullOrEmpty(tenantId) ||
                    (!string.Equals(roleTenant, tenantId, StringComparison.OrdinalIgnoreCase) && roleTenant != "*"))
                {
                    continue;
                }

                if (GatewayRoleExtensions.TryParseRole(roleName, out var parsedRole))
                {
                    // Security Guard: ClusterAdmin and GovernanceAdmin are strictly global platform roles.
                    // They can never be granted via a tenant-scoped prefix (e.g. "tenant-1:ClusterAdmin").
                    if (parsedRole is GatewayRole.ClusterAdmin or GatewayRole.GovernanceAdmin)
                    {
                        continue;
                    }

                    result.Add(parsedRole);
                }
            }
            else
            {
                // Global role (applies across all tenants)
                if (GatewayRoleExtensions.TryParseRole(raw, out var parsedRole))
                {
                    result.Add(parsedRole);
                }
            }
        }

        return result;
    }
}
