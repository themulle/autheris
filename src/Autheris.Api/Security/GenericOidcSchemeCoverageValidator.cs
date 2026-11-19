namespace Autheris.Api.Security;

using System;
using System.Collections.Generic;
using Autheris.Domain.Identity;
using Autheris.Domain.Options.Security;
using Microsoft.Extensions.Options;

/// <summary>
/// A GenericOidc realm with subject roles or a static JWKS authenticates only through its own realm scheme. When such a realm has no scheme
/// (the realm scheme registration reads the host configuration), startup fails instead of leaving the realm unauthenticated or unmapped.
/// </summary>
public sealed class GenericOidcSchemeCoverageValidator(GenericOidcSchemeMap map) : IValidateOptions<IdentityRealmsOptions>
{
    public ValidateOptionsResult Validate(string? name, IdentityRealmsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        foreach (var (realmId, realm) in options.Realms)
        {
            if (realm.Kind != IdentityRealmKind.GenericOidc || (realm.SubjectRoles is not { Count: > 0 } && realm.Jwks is null))
            {
                continue;
            }

            var scheme = GenericOidcSchemeMap.SchemeName(realmId);
            foreach (var issuer in realm.Issuers ?? [])
            {
                if (!map.TryGetScheme(issuer, out var registered) || !string.Equals(registered, scheme, StringComparison.Ordinal))
                {
                    failures.Add($"Authentication:Realms:Realms:{realmId} has no realm authentication scheme; define it in the host configuration, not only through code.");
                    break;
                }
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
