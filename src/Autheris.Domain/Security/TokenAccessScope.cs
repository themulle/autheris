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
}
