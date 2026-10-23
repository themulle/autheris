namespace Autheris.Domain.Identity;

using System;
using System.Collections.Generic;

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
