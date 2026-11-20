namespace Autheris.Api.Security;

using System.Security.Claims;
using Autheris.Domain.Options.Security;
using Autheris.Domain.Security;

/// <summary>
/// Marks validated GenericOidc realm tokens as read-only when they carry at least one scope and every scope
/// is contained in the realm's effective <see cref="IdentityRealmConfigOptions.ReadOnlyScopes"/> list.
/// Tokens with no scope claim are left unmarked so operator-configured workload roles continue to govern access.
/// Never throws or rejects a token.
/// </summary>
internal static class RealmTokenScopePolicy
{
    internal static void Apply(ClaimsPrincipal? principal, IdentityRealmConfigOptions? realm)
    {
        if (principal is null || realm is null)
        {
            return;
        }

        var scopes = TokenAccessScope.GetTokenScopes(principal);
        if (scopes.Count == 0)
        {
            return;
        }

        var readOnlyScopes = realm.GetEffectiveReadOnlyScopes();
        if (readOnlyScopes.Count == 0)
        {
            return;
        }

        for (var i = 0; i < scopes.Count; i++)
        {
            var tokenScope = scopes[i];
            var matched = false;
            for (var j = 0; j < readOnlyScopes.Count; j++)
            {
                if (TokenAccessScope.IsSameScope(readOnlyScopes[j], tokenScope))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return;
            }
        }

        foreach (var identity in principal.Identities)
        {
            TokenAccessScope.MarkReadOnly(identity);
        }
    }
}
