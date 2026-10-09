namespace Autheris.Api.Security;

using System;
using System.Linq;
using System.Security.Claims;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;

/// <summary>
/// Finding 3.3 / R13: applies scope and token-kind rules to a validated Entra token.
/// App-only tokens follow <see cref="EntraIdAuthOptions.AppOnlyTokens"/>; user tokens limited to
/// <see cref="EntraIdAuthOptions.ReadOnlyScopes"/> become read-only.
/// </summary>
public static class EntraTokenPolicy
{
    /// <returns>A failure reason when the token must be rejected, otherwise <c>null</c>.</returns>
    public static string? Apply(ClaimsPrincipal principal, EntraIdAuthOptions options, IIdentitySubjectResolver subjectResolver)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity is not ClaimsIdentity identity)
        {
            return null;
        }

        // SG-08: Check for scopes (scp) and roles (roles).
        var scopes = TokenAccessScope.GetScopes(principal);
        var roles = principal.FindAll(options.RolesClaimType ?? "roles")
            .Concat(principal.FindAll(ClaimTypes.Role))
            .Select(c => c.Value)
            .ToList();

        // 1. Tokens without scp and without roles cannot access the API.
        // Also detects and rejects ID-tokens passed as access tokens (which lack scp/roles).
        if (scopes.Count == 0 && roles.Count == 0)
        {
            return "Token without scopes ('scp') or roles ('roles') is not accepted as an API access token.";
        }

        // 2. Reject ID tokens explicitly: if token has 'nonce' claim typical of ID tokens.
        if (principal.HasClaim(c => c.Type == "nonce"))
        {
            return "ID tokens are not accepted as access tokens.";
        }

        // Delegated Entra tokens always carry scp; only tokens without it can be app-only.
        if (scopes.Count == 0 && subjectResolver.ResolveSubject(principal).Type == SubjectType.ServicePrincipal)
        {
            // App-only tokens must have at least one application role assigned
            if (roles.Count == 0)
            {
                return "App-only tokens require an assigned application role ('roles').";
            }

            // Client ID allowlist check (if configured)
            if (options.AllowedClientIds.Count > 0)
            {
                var clientId = principal.FindFirst("azp")?.Value
                    ?? principal.FindFirst("appid")?.Value
                    ?? principal.FindFirst("client_id")?.Value;

                if (string.IsNullOrWhiteSpace(clientId) ||
                    !options.AllowedClientIds.Contains(clientId, StringComparer.OrdinalIgnoreCase))
                {
                    return $"Client application '{clientId}' is not in the list of allowed app-only clients.";
                }
            }

            switch (options.AppOnlyTokens)
            {
                case AppOnlyTokenAccess.Deny:
                    return "App-only tokens are not accepted.";
                case AppOnlyTokenAccess.ReadOnly:
                    TokenAccessScope.MarkReadOnly(identity);
                    break;
            }

            return null;
        }

        if (scopes.Count > 0 && options.ReadOnlyScopes.Count > 0 &&
            scopes.All(s => options.ReadOnlyScopes.Any(r => IsSameScope(r, s))))
        {
            TokenAccessScope.MarkReadOnly(identity);
        }

        return null;
    }

    // scp carries the bare scope name; the configuration may use the full form api://app/Agent.Read.
    private static bool IsSameScope(string configured, string tokenScope)
    {
        var bare = configured.Trim();
        var slash = bare.LastIndexOf('/');
        if (slash >= 0 && slash < bare.Length - 1)
        {
            bare = bare[(slash + 1)..];
        }

        return string.Equals(bare, tokenScope, StringComparison.OrdinalIgnoreCase);
    }
}
