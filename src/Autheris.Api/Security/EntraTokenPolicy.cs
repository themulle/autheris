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

        // Delegated Entra tokens always carry scp; only tokens without it can be app-only.
        var scopes = TokenAccessScope.GetScopes(principal);
        if (scopes.Count == 0 && subjectResolver.ResolveSubject(principal).Type == SubjectType.ServicePrincipal)
        {
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
