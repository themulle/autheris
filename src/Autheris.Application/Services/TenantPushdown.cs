namespace Autheris.Application.Services;

using System;
using System.Linq;
using System.Security;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Security;

/// <summary>
/// MISC-13: the one tenant an executor may push down to an external system is the gateway-resolved tenant of the execution context
/// (realm tenant binding, cluster-admin selection). Raw token claims are never a tenant source here.
/// </summary>
internal static class TenantPushdown
{
    /// <summary>The context tenant; fail closed (<see cref="SecurityException"/>) when it is missing.</summary>
    public static string Require(TenantId? tenant, string what)
    {
        if (tenant is not { } resolved || string.IsNullOrWhiteSpace(resolved.Value))
        {
            throw new SecurityException($"The HTTP data source requires a tenant ({what}), but the execution context carries no resolved tenant.");
        }

        return resolved.Value;
    }

    /// <summary>
    /// True when the token carries a raw tenant claim that differs (ordinal) from the resolved tenant and the caller is not a ClusterAdmin
    /// (a ClusterAdmin legitimately selects another tenant). The resolved tenant stays authoritative; the caller audits the mismatch.
    /// </summary>
    public static bool HasConflictingRawClaim(ClaimsPrincipal principal, string resolvedTenant)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.GetUserRoles().Contains("ClusterAdmin", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return principal.Claims.Any(c =>
            (string.Equals(c.Type, "tenant_id", StringComparison.Ordinal)
             || string.Equals(c.Type, "tid", StringComparison.Ordinal)
             || string.Equals(c.Type, "tenant", StringComparison.Ordinal))
            && !string.IsNullOrWhiteSpace(c.Value)
            && !string.Equals(c.Value, resolvedTenant, StringComparison.Ordinal));
    }
}
