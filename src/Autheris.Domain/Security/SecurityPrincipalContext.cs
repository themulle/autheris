namespace Autheris.Domain.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;

/// <summary>
/// Architecture Phase 1: Canonical, immutable security context associated with an individual request.
/// Eliminates ad-hoc claim lookups, header spoofing, and diverging identity normalization.
/// </summary>
public sealed record SecurityPrincipalContext
{
    public const string ItemKey = "SecurityPrincipalContext";

    public required Sid UserSid { get; init; }
    public required TenantId TenantId { get; init; }
    public required IReadOnlySet<Sid> GroupSids { get; init; }
    public required IReadOnlySet<string> TenantRoles { get; init; }
    public required IReadOnlySet<string> ClusterRoles { get; init; }
    public required string AuthenticationScheme { get; init; }
    public bool IsAuthenticated { get; init; } = true;
    public string? BreakGlassJustification { get; init; }
    public System.Net.IPAddress? ClientIp { get; init; }

    /// <summary>Cluster-level administrator authorized to perform cross-tenant operations.</summary>
    public bool IsClusterAdmin => ClusterRoles.Contains("ClusterAdmin");

    /// <summary>Returns true if the principal possesses any of the specified roles in either tenant or cluster scope.</summary>
    public bool HasAnyRole(params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return roles.Any(r => TenantRoles.Contains(r) || ClusterRoles.Contains(r));
    }

    /// <summary>Returns true if the principal possesses any of the specified roles in either tenant or cluster scope.</summary>
    public bool HasAnyRole(IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return roles.Any(r => TenantRoles.Contains(r) || ClusterRoles.Contains(r));
    }

    /// <summary>Checks whether the principal has a specific role in tenant or cluster scope.</summary>
    public bool HasRole(string role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return TenantRoles.Contains(role) || ClusterRoles.Contains(role);
    }

    /// <summary>K-K10: Checks whether the principal satisfies the required GatewayRole with hierarchy semantics.</summary>
    public bool HasRole(GatewayRole role)
    {
        if (IsClusterAdmin) return true;
        foreach (var r in TenantRoles.Concat(ClusterRoles))
        {
            if (GatewayRoleExtensions.TryParseRole(r, out var parsed) && parsed.Implies(role))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>K-K10: Checks whether the principal satisfies any of the required GatewayRoles.</summary>
    public bool HasAnyRole(params GatewayRole[] roles) => HasAnyRole((IEnumerable<GatewayRole>)roles);

    /// <summary>K-K10: Checks whether the principal satisfies any of the required GatewayRoles.</summary>
    public bool HasAnyRole(IEnumerable<GatewayRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        foreach (var required in roles)
        {
            if (HasRole(required))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Creates an anonymous security context with LegacySingleTenant and empty permissions.</summary>
    public static SecurityPrincipalContext Anonymous(TenantId? tenantId = null) => new()
    {
        UserSid = new Sid("ANONYMOUS"),
        TenantId = tenantId ?? TenantId.LegacySingleTenant,
        GroupSids = new HashSet<Sid>(),
        TenantRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        ClusterRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        AuthenticationScheme = "Anonymous",
        IsAuthenticated = false
    };
}
