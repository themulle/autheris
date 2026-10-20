namespace Autheris.Api.Security;

using System;
using Autheris.Application.Identity;
using Microsoft.IdentityModel.JsonWebTokens;

public interface IRealmSchemeSelector
{
    string SelectScheme(string token);
}

public sealed class RealmSchemeSelector(IIdentityRealmRegistry realmRegistry) : IRealmSchemeSelector
{
    private static readonly JsonWebTokenHandler TokenHandler = new();

    public string SelectScheme(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        try
        {
            var jwt = TokenHandler.ReadJsonWebToken(token.Trim());
            var issuer = jwt.Issuer;

            if (string.IsNullOrWhiteSpace(issuer))
            {
                return GatewayAuthSchemes.RejectScheme;
            }

            var realm = realmRegistry.ResolveRealmByIssuer(issuer);
            if (realm == null)
            {
                // Unknown issuer: Route to RejectScheme (fail closed)
                return GatewayAuthSchemes.RejectScheme;
            }

            // Return realm scheme
            return GatewayAuthSchemes.RealmJwt(realm.RealmId);
        }
        catch
        {
            // Malformed token: reject
            return GatewayAuthSchemes.RejectScheme;
        }
    }
}
