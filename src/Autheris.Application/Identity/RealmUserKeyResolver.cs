namespace Autheris.Application.Identity;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;

/// <summary>
/// Resolves the canonical user key of an authenticated principal according to the realm's configured <see cref="IdentityRealm.UserKeyClaim"/>,
/// never falling back to shared client/application claims or mutable profile claims.
/// </summary>
public static class RealmUserKeyResolver
{
    public const string EntraObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    private static readonly string[] WindowsSidFallbackClaimTypes =
    [
        ClaimTypes.PrimarySid,
        "objectSid",
        "onprem_sid",
        "primarysid",
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid"
    ];

    public static string? Resolve(ClaimsPrincipal? principal, IdentityRealm? realm, string? correlatedUserKey = null) =>
        !string.IsNullOrWhiteSpace(correlatedUserKey)
            ? correlatedUserKey.Trim()
            : principal is null
                ? null
                : ResolveCore(principal.FindAll, () => principal.GetUserSid()?.Value, realm);

    public static string? Resolve(ClaimsIdentity? identity, IdentityRealm? realm) =>
        identity is null
            ? null
            : ResolveCore(identity.FindAll, () => FirstSidFallback(identity.FindAll), realm);

    private static string? ResolveCore(Func<string, IEnumerable<Claim>> findAll, Func<string?> getUserSid, IdentityRealm? realm)
    {
        var claimName = string.IsNullOrWhiteSpace(realm?.UserKeyClaim) ? "oid" : realm.UserKeyClaim.Trim();
        string? resolved;
        if (string.Equals(claimName, "sub", StringComparison.OrdinalIgnoreCase))
        {
            resolved = FindFirstNonEmpty(findAll("sub"), false) ?? FindFirstNonEmpty(findAll(ClaimTypes.NameIdentifier), true);
        }
        else if (string.Equals(claimName, "oid", StringComparison.OrdinalIgnoreCase))
        {
            resolved = FindFirstNonEmpty(findAll("oid"), false)
                ?? FindFirstNonEmpty(findAll(EntraObjectIdClaim), false)
                ?? FindFirstNonEmpty(findAll("sub"), false)
                ?? FindFirstNonEmpty(findAll(ClaimTypes.NameIdentifier), false)
                ?? getUserSid();
        }
        else
        {
            resolved = FindFirstNonEmpty(findAll(claimName), false);
        }

        return string.IsNullOrWhiteSpace(resolved) ? null : resolved.Trim();
    }

    private static string? FirstSidFallback(Func<string, IEnumerable<Claim>> findAll)
    {
        foreach (var type in WindowsSidFallbackClaimTypes)
        {
            if (FindFirstNonEmpty(findAll(type), false) is { } found) return found;
        }
        return FindFirstNonEmpty(findAll("oid"), false)
            ?? FindFirstNonEmpty(findAll(EntraObjectIdClaim), false)
            ?? FindFirstNonEmpty(findAll(ClaimTypes.NameIdentifier), false)
            ?? FindFirstNonEmpty(findAll("sub"), false);
    }

    private static string? FindFirstNonEmpty(IEnumerable<Claim> claims, bool excludeSynthesized)
    {
        foreach (var claim in claims)
        {
            if (excludeSynthesized && string.Equals(claim.Issuer, IdentityClaimIssuers.FallbackNameIdentifier, StringComparison.Ordinal))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(claim.Value))
            {
                return claim.Value;
            }
        }
        return null;
    }
}
