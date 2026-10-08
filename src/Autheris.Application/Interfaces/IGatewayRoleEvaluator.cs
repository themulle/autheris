namespace Autheris.Application.Interfaces;

using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Domain.Security;

/// <summary>
/// K-K10: Single Source of Truth for Enterprise RBAC evaluation across the Gateway.
/// Resolves and evaluates effective roles considering hierarchy and tenant qualification.
/// </summary>
public interface IGatewayRoleEvaluator
{
    bool HasRole(ClaimsPrincipal? principal, GatewayRole role, string? tenantId = null);
    bool HasRole(ClaimsPrincipal? principal, string roleName, string? tenantId = null);
    bool HasAnyRole(ClaimsPrincipal? principal, IEnumerable<GatewayRole> roles, string? tenantId = null);
    bool HasAnyRole(ClaimsPrincipal? principal, IEnumerable<string> roleNames, string? tenantId = null);
    bool HasExactRole(ClaimsPrincipal? principal, string roleName, string? tenantId = null);
    bool HasAnyExactRole(ClaimsPrincipal? principal, IEnumerable<string> roleNames, string? tenantId = null);
    IReadOnlySet<GatewayRole> GetEffectiveRoles(ClaimsPrincipal? principal, string? tenantId = null);
}
