namespace Autheris.Domain.Security;

using System;
using System.Linq;
using System.Security.Claims;

/// <summary>
/// RR-L2-01 / RR-L4-02 / RV-03: Single definition of "cluster administrator" used for every cross-tenant decision.
/// Only the literal, unprefixed role <c>ClusterAdmin</c> grants cross-tenant rights. Aliases such as
/// <c>GatewayAdmin</c>/<c>PlatformAdmin</c> are tenant-scoped administrators (they may administer their own tenant but
/// never select another one), and identities asserted by ForwardAuth proxy headers are never cluster admins.
/// </summary>
public static class ClusterAdminPolicy
{
    /// <summary>Authentication type / scheme name of the ForwardAuth handler.</summary>
    public const string ForwardAuthScheme = "ForwardAuth";

    public static bool IsCanonicalClusterAdmin(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (principal.Identities.Any(i => i.IsAuthenticated &&
                                          string.Equals(i.AuthenticationType, ForwardAuthScheme, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return principal.Claims
            .Where(c => c.Type == ClaimTypes.Role || c.Type == "role" || c.Type == "roles")
            .Any(c => IsClusterAdminRole(c.Value));
    }

    // Tenant-prefixed roles ("tenant:ClusterAdmin") are tenant-scoped and never grant cluster-wide rights.
    public static bool IsClusterAdminRole(string? role) =>
        string.Equals(role?.Trim(), "ClusterAdmin", StringComparison.OrdinalIgnoreCase);
}
