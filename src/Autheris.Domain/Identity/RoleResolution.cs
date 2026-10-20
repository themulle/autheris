namespace Autheris.Domain.Identity;

using System;
using System.Collections.Generic;

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
}
