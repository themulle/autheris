namespace Autheris.Domain.Identity;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Security;

/// <summary>GOV-08: whether every configured role provider answered, or the answer is incomplete and must not be trusted or cached.</summary>
public enum RoleResolutionStatus
{
    Resolved = 0,
    Degraded = 1
}

/// <summary>GOV-08: the claim that marks a request whose group and role resolution was unavailable; the PDP denies data access on it.</summary>
public static class RoleResolutionClaims
{
    public const string ClaimType = "autheris:role_resolution";
    public const string Degraded = "degraded";
    public const string AllowedGatewayRolesClaimType = "autheris:allowed_gateway_roles";
    private const string EmptyAllowedRolesSentinel = "__none__";

    public static Claim CreateAllowedGatewayRolesClaim(IEnumerable<GatewayRole> allowedRoles)
    {
        ArgumentNullException.ThrowIfNull(allowedRoles);
        var values = allowedRoles.Distinct().Select(r => r.ToString()).OrderBy(s => s, StringComparer.Ordinal);
        var joined = string.Join(',', values);
        return new Claim(
            AllowedGatewayRolesClaimType,
            string.IsNullOrEmpty(joined) ? EmptyAllowedRolesSentinel : joined,
            ClaimValueTypes.String,
            IdentityClaimIssuers.Pipeline);
    }

    public static bool TryReadAllowedGatewayRoles(ClaimsPrincipal? principal, out HashSet<GatewayRole> allowedRoles)
    {
        allowedRoles = [];
        if (principal is null)
        {
            return false;
        }

        bool found = false;
        foreach (var claim in principal.Claims)
        {
            if (!string.Equals(claim.Type, AllowedGatewayRolesClaimType, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(claim.Issuer, IdentityClaimIssuers.Pipeline, StringComparison.Ordinal))
            {
                continue;
            }

            var parsed = new HashSet<GatewayRole>();
            if (!string.IsNullOrWhiteSpace(claim.Value) &&
                !string.Equals(claim.Value.Trim(), EmptyAllowedRolesSentinel, StringComparison.Ordinal))
            {
                foreach (var token in claim.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (GatewayRoleExtensions.TryParseGlobalRole(token, out var role))
                    {
                        parsed.Add(role);
                    }
                }
            }

            if (!found)
            {
                allowedRoles = parsed;
                found = true;
            }
            else
            {
                allowedRoles.IntersectWith(parsed);
            }
        }

        return found;
    }
}

public sealed record RoleResolution(
    IReadOnlySet<string> Groups,
    IReadOnlySet<string> AppRoles,
    IReadOnlyDictionary<string, string> Attributes,
    DateTimeOffset ResolvedAt,
    DateTimeOffset ExpiresAt,
    string Provenance
)
{
    public IReadOnlySet<string> Roles => AppRoles;

    /// <summary><see cref="RoleResolutionStatus.Degraded"/> when a provider failed or threw: the groups may be incomplete.</summary>
    public RoleResolutionStatus Status { get; init; } = RoleResolutionStatus.Resolved;

    public bool IsDegraded => Status == RoleResolutionStatus.Degraded;

    public static readonly RoleResolution Empty = EmptyWithExpiry(DateTimeOffset.MaxValue);

    public static RoleResolution EmptyWithExpiry(DateTimeOffset expiresAt) =>
        new(new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow,
            expiresAt,
            "empty");
}

public sealed record ProviderRoleResult(
    IReadOnlySet<string> Groups,
    IReadOnlySet<string> AppRoles,
    IReadOnlyDictionary<string, string> Attributes
)
{
    public static readonly ProviderRoleResult Empty = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>GOV-08: true when the provider could not answer (exception, throttling, open circuit breaker, not configured); the groups are then unknown, not empty.</summary>
    public bool Failed { get; init; }

    /// <summary>The provider failed: distinct from <see cref="Empty"/>, which is a genuine "no groups" answer.</summary>
    public static readonly ProviderRoleResult FailedResult = Empty with { Failed = true };
}
