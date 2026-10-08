namespace Autheris.Api.Security;

public static class GatewayAuthSchemes
{
    public const string DefaultScheme = "GatewayDynamicScheme";
    public const string Basic = "Basic";
    public const string ForwardAuth = "ForwardAuth";
    public const string JwtBearer = "Bearer";

    /// <summary>API-14: separate scheme for AD FS when Entra ID is enabled as well.</summary>
    public const string JwtBearerAdfs = "BearerAdfs";

    /// <summary>
    /// API-14: chooses the JWT scheme for a bearer token. Only routing: the issuer is read without validation, the selected
    /// scheme validates issuer, audience and signature. Without both IdPs enabled there is a single scheme.
    /// </summary>
    public static string SelectJwtScheme(string token, Autheris.Domain.Options.GatewayOptions options)
    {
        System.ArgumentNullException.ThrowIfNull(options);
        var adfs = options.Authentication.Adfs;
        if (!options.Authentication.EntraId.Enabled || !adfs.Enabled || string.IsNullOrWhiteSpace(adfs.Authority))
        {
            return JwtBearer;
        }

        try
        {
            var issuer = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().ReadJsonWebToken(token.Trim()).Issuer;
            var authority = adfs.Authority.TrimEnd('/');
            return issuer != null && issuer.StartsWith(authority, System.StringComparison.OrdinalIgnoreCase)
                ? JwtBearerAdfs
                : JwtBearer;
        }
        catch (System.ArgumentException)
        {
            return JwtBearer; // not a JWT: the default scheme rejects it
        }
    }
}
