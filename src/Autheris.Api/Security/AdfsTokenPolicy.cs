namespace Autheris.Api.Security;

using System;
using System.Security.Claims;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Security;

/// <summary>
/// Client allowlist and read-only scope policy for validated AD FS access tokens. Fail-closed: an empty allowlist,
/// a missing client id claim or an unlisted client rejects the token (startup requires a non-empty list outside Development).
/// Accepted tokens carrying only read-only scopes are marked read-only.
/// </summary>
public static class AdfsTokenPolicy
{
    /// <returns>A failure reason when the token must be rejected, otherwise <c>null</c>.</returns>
    public static string? Apply(ClaimsPrincipal principal, AdfsAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(options);

        var clientId = principal.FindFirst("appid")?.Value
            ?? principal.FindFirst("azp")?.Value
            ?? principal.FindFirst("client_id")?.Value;

        if (string.IsNullOrWhiteSpace(clientId))
        {
            return "Token carries no client application id.";
        }

        foreach (var allowed in options.AllowedClientIds)
        {
            if (string.Equals(allowed, clientId, StringComparison.OrdinalIgnoreCase))
            {
                RealmTokenScopePolicy.ApplyScopes(principal, IdentityRealmConfigOptions.DefaultReadOnlyScopes);
                return null;
            }
        }

        return "Client application is not in the list of allowed clients.";
    }
}
