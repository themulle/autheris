namespace Autheris.Domain.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;

/// <summary>
/// Finding 3.3 / R13: marks principals whose token only permits reading (delegated agent scope or app-only token).
/// The marker carries a fixed issuer that no token can supply, so it cannot be forged through an inbound claim.
/// </summary>
public static class TokenAccessScope
{
    public const string ClaimType = "autheris:access_mode";
    public const string ReadOnlyValue = "read";
    public const string Issuer = "Autheris:TokenAccessScope";

    private static readonly string[] ScopeClaimTypes =
    [
        "scp",
        "http://schemas.microsoft.com/identity/claims/scope"
    ];

    public static bool IsReadOnly(this ClaimsPrincipal? principal) =>
        principal?.HasClaim(c => c.Type == ClaimType && c.Value == ReadOnlyValue && c.Issuer == Issuer) == true;

    /// <summary>
    /// Checks whether the principal carries the specified delegated scope (comparing bare names).
    /// </summary>
    public static bool HasScope(this ClaimsPrincipal? principal, string scope)
    {
        if (principal == null || string.IsNullOrWhiteSpace(scope)) return false;
        var bareTarget = ScopeNames.ToBareName(scope.Trim());
        return GetScopes(principal).Any(s => string.Equals(ScopeNames.ToBareName(s), bareTarget, StringComparison.OrdinalIgnoreCase));
    }

    public static void MarkReadOnly(ClaimsIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.HasClaim(c => c.Type == ClaimType && c.Issuer == Issuer))
        {
            identity.AddClaim(new Claim(ClaimType, ReadOnlyValue, ClaimValueTypes.String, Issuer));
        }
    }

    /// <summary>Delegated scopes of the token (space-separated <c>scp</c>, raw or mapped by JwtBearer).</summary>
    public static IReadOnlyList<string> GetScopes(ClaimsPrincipal principal) =>
        ScopeClaimTypes
            .SelectMany(principal.FindAll)
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static IReadOnlySet<string> GetScopeSet(ClaimsPrincipal? principal)
    {
        if (principal == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(GetScopes(principal), StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlySet<string> GetScopeSet(IEnumerable<Claim>? claims)
    {
        if (claims == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in claims)
        {
            if (ScopeClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase) ||
                string.Equals(claim.Type, "scope", StringComparison.OrdinalIgnoreCase))
            {
                var parts = claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var part in parts)
                {
                    set.Add(part);
                }
            }
        }
        return set;
    }
}
