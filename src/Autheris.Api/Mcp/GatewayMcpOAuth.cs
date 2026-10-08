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
    /// <summary>Only with MCP and at least one OAuth issuer enabled; Kerberos, Basic and ForwardAuth have nothing to discover.</summary>
    public static bool IsEnabled(GatewayOptions options) =>
        options.Mcp.Enabled && (options.Authentication.EntraId.Enabled || options.Authentication.Adfs.Enabled);

    public static AuthenticationBuilder AddGatewayMcpOAuth(this AuthenticationBuilder builder, GatewayOptions options)
    {
        if (!IsEnabled(options) || AuthorizationServers(options).Count == 0)
        {
            return builder;
        }

        var scopes = new List<string>();
        var entra = options.Authentication.EntraId;
        if (entra.Enabled && !string.IsNullOrWhiteSpace(entra.Audience))
        {
            scopes.Add(entra.Audience.TrimEnd('/') + "/.default");
        }

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
