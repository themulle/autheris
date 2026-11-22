namespace Autheris.Application.Catalog.External;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Security;

/// <summary>
/// The one predicate that decides whether a token belongs to an application (service principal) rather than a person. Used by the API
/// authorization layer and by the external catalog reader so the two can never drift. The decision is fail-closed: an ambiguous token
/// is a person.
/// </summary>
public static class AppOnlyPrincipal
{
    /// <summary>Claim types that identify a person; a token carrying one of them is never classified by the client-id heuristic.</summary>
    public static readonly IReadOnlyList<string> PersonIdClaimTypes =
    [
        ClaimTypes.PrimarySid,
        "objectSid",
        "onprem_sid",
        "primarysid",
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid",
        "oid",
        "http://schemas.microsoft.com/identity/claims/objectidentifier",
        ClaimTypes.Upn,
        "upn",
        "preferred_username",
        ClaimTypes.Email,
        "email"
    ];

    /// <summary>
    /// True for a token the issuer marks as an application (<c>idtyp=app</c> or a client-credentials grant type), a
    /// <c>service-account-</c> username, or a token that has a client id, no scope and no person identifier at all.
    /// </summary>
    public static bool Is(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return false;
        }

        if (string.Equals(principal.FindFirst("idtyp")?.Value, "app", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var gty = principal.FindFirst("gty")?.Value;
        if (string.Equals(gty, "client-credentials", StringComparison.OrdinalIgnoreCase)
            || string.Equals(gty, "client_credentials", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (principal.FindFirst("preferred_username")?.Value?.StartsWith("service-account-", StringComparison.OrdinalIgnoreCase) == true
            && !string.IsNullOrWhiteSpace(GetClientId(principal))
            && TokenAccessScope.GetScopeSet(principal.Claims).Count == 0
            && principal.GetUserSid() is null)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(GetClientId(principal))
            && TokenAccessScope.GetScopeSet(principal.Claims).Count == 0
            && principal.GetUserSid() is null
            && !HasAnyClaim(principal, PersonIdClaimTypes);
    }

    /// <summary>The client id of an application token or identity (<c>azp</c>, <c>appid</c> or <c>client_id</c>), or null.</summary>
    public static string? GetClientId(ClaimsPrincipal? principal) =>
        principal?.FindFirst("azp")?.Value ?? principal?.FindFirst("appid")?.Value ?? principal?.FindFirst("client_id")?.Value;

    public static string? GetClientId(ClaimsIdentity? identity) =>
        identity?.FindFirst("azp")?.Value ?? identity?.FindFirst("appid")?.Value ?? identity?.FindFirst("client_id")?.Value;

    /// <summary>True when the principal carries a non-empty value for any of the claim types.</summary>
    public static bool HasAnyClaim(ClaimsPrincipal principal, IEnumerable<string> claimTypes)
    {
        foreach (var type in claimTypes)
        {
            if (!string.IsNullOrWhiteSpace(principal.FindFirst(type)?.Value))
            {
                return true;
            }
        }

        return false;
    }
}
