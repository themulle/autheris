namespace Autheris.Api.Security;

using System;
using System.Collections.Generic;

/// <summary>
/// Maps the exact issuers of the configured GenericOidc realms to their JwtBearer scheme names. Built once at startup from configuration;
/// an issuer that is not listed has no scheme here and is rejected by the Entra / AD FS schemes' issuer validation.
/// </summary>
public sealed class GenericOidcSchemeMap
{
    public const string SchemePrefix = "Bearer:realm:";

    private readonly Dictionary<string, string> _schemeByIssuer = new(StringComparer.Ordinal);

    public bool IsEmpty => _schemeByIssuer.Count == 0;

    public static string SchemeName(string realmId) => SchemePrefix + realmId;

    internal void Add(string issuer, string scheme)
    {
        if (!_schemeByIssuer.TryAdd(issuer, scheme))
        {
            throw new InvalidOperationException("A GenericOidc issuer is configured for more than one realm.");
        }
    }

    public bool TryGetScheme(string issuer, out string scheme)
    {
        if (issuer is not null && _schemeByIssuer.TryGetValue(issuer, out var found))
        {
            scheme = found;
            return true;
        }

        scheme = string.Empty;
        return false;
    }
}
