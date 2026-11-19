namespace Autheris.Application.Identity;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Domain.Identity;
using Autheris.Domain.Security;

/// <summary>
/// Decides which gateway roles a workload subject receives from the realm's configured subject mapping. Pure and allocation-light:
/// the mapping is an immutable snapshot built at startup, nothing here can create or change a mapping.
/// </summary>
public static class SubjectRoleMapper
{
    public const int MaxEntries = 32;
    public const int MaxRolesPerEntry = 8;
    public const int MaxSubjectLength = 256;

    /// <summary>The characters that can never appear in a mapped subject (pattern and wildcard syntax).</summary>
    public const string ForbiddenSubjectCharacters = "*?%[]^$|";

    /// <summary>Strict role-name parse: an exact name of <see cref="GatewayRole"/> (case-insensitive); numbers and unknown names are rejected.</summary>
    public static bool TryParseRoleName(string? name, out GatewayRole role)
    {
        role = default;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var candidate in Enum.GetValues<GatewayRole>())
        {
            if (string.Equals(candidate.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                role = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The mapped roles for the identity, or an empty list. Applies only when the realm is a GenericOidc realm that keys users by
    /// <c>sub</c> with a fixed tenant, the token issuer is one of the realm's own issuers (exact), the token carries exactly one
    /// <c>sub</c> claim and that subject equals a configured subject exactly (ordinal). Roles outside the realm ceiling are never returned.
    /// </summary>
    public static IReadOnlyList<GatewayRole> Resolve(IdentityRealm? realm, ClaimsIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (realm?.SubjectRoles is not { Count: > 0 } mapping
            || realm.Kind != IdentityRealmKind.GenericOidc
            || !string.Equals(realm.UserKeyClaim, "sub", StringComparison.Ordinal)
            || realm.TenantBinding.Kind != TenantBindingKind.Fixed)
        {
            return [];
        }

        var issuer = SingleValue(identity, "iss");
        if (issuer is null || !ContainsOrdinal(realm.Issuers, issuer))
        {
            return [];
        }

        var subject = SingleValue(identity, "sub");
        if (subject is null || subject.Length > MaxSubjectLength || !mapping.TryGetValue(subject, out var roles))
        {
            return [];
        }

        // sub is the only subject statement: a differing NameIdentifier claim is an ambiguous identity.
        foreach (var claim in identity.FindAll(ClaimTypes.NameIdentifier))
        {
            if (!string.Equals(claim.Value, subject, StringComparison.Ordinal))
            {
                return [];
            }
        }

        var allowed = new List<GatewayRole>(roles.Count);
        foreach (var role in roles)
        {
            if (!realm.AllowedGatewayRoles.Contains(role))
            {
                continue;
            }

            if (role == GatewayRole.ClusterAdmin && !realm.AllowPrivilegedSubjectRoles)
            {
                continue;
            }

            allowed.Add(role);
        }

        return allowed;
    }

    private static string? SingleValue(ClaimsIdentity identity, string type)
    {
        string? value = null;
        foreach (var claim in identity.FindAll(type))
        {
            if (value is not null)
            {
                return null;
            }

            value = claim.Value;
        }

        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static bool ContainsOrdinal(IReadOnlyList<string> values, string candidate)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
