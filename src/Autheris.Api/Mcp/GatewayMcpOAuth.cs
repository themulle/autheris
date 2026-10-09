namespace Autheris.Api.Mcp;

using System;
using System.Collections.Generic;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Authentication;

/// <summary>
/// OAuth discovery for MCP clients (RFC 9728 protected resource metadata, served at
/// <c>/.well-known/oauth-protected-resource/&lt;mcp path&gt;</c>). The authorization servers are the token issuers the
/// gateway already accepts (Entra ID, AD FS), so an MCP client can obtain a token without extra configuration.
/// </summary>
public static class GatewayMcpOAuth
{
    public static bool IsEnabled(GatewayOptions options) =>
        options.Mcp.Enabled &&
        (options.Authentication.EntraId.Enabled || options.Authentication.Adfs.Enabled) &&
        AuthorizationServers(options).Count > 0;

    /// <summary>Checks whether OAuth discovery routes should be exposed.</summary>
    public static bool CanDiscover(GatewayOptions options) =>
        options.Mcp.Enabled &&
        (options.Authentication.EntraId.Enabled || options.Authentication.Adfs.Enabled);

    public static List<string> GetSupportedScopes(GatewayOptions options)
    {
        var scopes = new List<string> { "Agent.Read" };
        var entra = options.Authentication.EntraId;
        if (entra.Enabled && !string.IsNullOrWhiteSpace(entra.Audience))
        {
            var aud = entra.Audience.TrimEnd('/');
            scopes.Add($"{aud}/Agent.Read");
        }
        return scopes;
    }

    public static AuthenticationBuilder AddGatewayMcpOAuth(this AuthenticationBuilder builder, GatewayOptions options)
    {
        if (!IsEnabled(options))
        {
            return builder;
        }

        var scopes = GetSupportedScopes(options);

        return builder.AddMcp(o =>
        {
            o.ResourceMetadata = new ProtectedResourceMetadata
            {
                AuthorizationServers = AuthorizationServers(options),
                ScopesSupported = scopes,
                BearerMethodsSupported = ["header"],
                ResourceName = "Autheris MCP"
            };
        });
    }

    /// <summary>Same issuer URLs as the JWT bearer validation (GatewayServiceCollectionExtensions.ConfigureJwtBearer).</summary>
    internal static List<string> AuthorizationServers(GatewayOptions options)
    {
        var servers = new List<string>();
        var entra = options.Authentication.EntraId;
        if (entra.Enabled && !string.IsNullOrWhiteSpace(entra.TenantId))
        {
            var instance = string.IsNullOrWhiteSpace(entra.Instance) ? "https://login.microsoftonline.com/" : entra.Instance.TrimEnd('/') + "/";
            servers.Add($"{instance}{entra.TenantId}/v2.0");
        }

        var adfs = options.Authentication.Adfs;
        if (adfs.Enabled && !string.IsNullOrWhiteSpace(adfs.Authority))
        {
            servers.Add(adfs.Authority.TrimEnd('/'));
        }

        return servers;
    }
}
