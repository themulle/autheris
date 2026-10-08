namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Autheris.Api.Mcp;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static class McpEndpoints
{
    // SEC M-09: Hard body limit for JSON-RPC messages (independent of Content-Length / chunked encoding).
    internal const long MaxMcpMessageBytes = 1024 * 1024;

    /// <summary>
    /// SEC H-16: Identity of the current MCP caller, derived per request from the authenticated principal.
    /// </summary>
    internal sealed record McpCaller(
        string PrincipalId,
        string? UserSid,
        string TenantId,
        IReadOnlyList<string> Roles,
        IReadOnlyList<string> GroupSids,
        string? ClientIp = null,
        bool IsReadOnly = false,
        IReadOnlyDictionary<string, string>? AdditionalClaims = null);

    public static IEndpointRouteBuilder MapMcpEndpoints(
        this IEndpointRouteBuilder app,
        GatewayOptions gatewayOptions,
        IHostEnvironment? env = null)
    {
        if (!gatewayOptions.Mcp.Enabled)
        {
            return app;
        }

        var mcpBasePath = string.IsNullOrWhiteSpace(gatewayOptions.Mcp.EndpointPath)
            ? "/mcp"
            : gatewayOptions.Mcp.EndpointPath.TrimEnd('/');

        // Streamable HTTP on the official MCP SDK (GatewayMcpServer); stateless, so there are no session endpoints.
        var endpoint = app.MapMcp(mcpBasePath);

        // SEC M-09: hard body limit for JSON-RPC messages.
        endpoint.Add(builder =>
        {
            var inner = builder.RequestDelegate!;
            builder.RequestDelegate = async context =>
            {
                if (context.Request.ContentLength > MaxMcpMessageBytes)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await context.Response.WriteAsJsonAsync(new { error = "MCP message exceeds maximum allowed size (1 MB)." }, context.RequestAborted).ConfigureAwait(false);
                    return;
                }

                var sizeFeature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
                if (sizeFeature is { IsReadOnly: false })
                {
                    sizeFeature.MaxRequestBodySize = MaxMcpMessageBytes;
                }

                await inner(context).ConfigureAwait(false);
            };
        });

        // SEC H-02: OpenSchema no longer opens MCP. Only the explicit (production-blocked) MCP auth bypass does.
        if (gatewayOptions.IsMcpAuthBypassed)
        {
            endpoint.AllowAnonymous();
        }
        else
        {
            // The MCP scheme adds the OAuth protected resource metadata link to 401 responses (RFC 9728), so clients
            // find the authorization server; the gateway schemes authenticate the request.
            endpoint.RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
            {
                AuthenticationSchemes = GatewayMcpOAuth.IsEnabled(gatewayOptions)
                    // Order matters: the MCP challenge only sets its header; the gateway challenge then writes the body.
                    ? $"{ModelContextProtocol.AspNetCore.Authentication.McpAuthenticationDefaults.AuthenticationScheme},{Autheris.Api.Security.GatewayAuthSchemes.DefaultScheme}"
                    : Autheris.Api.Security.GatewayAuthSchemes.DefaultScheme
            });
        }

        if (GatewayMcpOAuth.IsEnabled(gatewayOptions))
        {
            var hostEnv = env ?? app.ServiceProvider?.GetService<IHostEnvironment>();
            bool isDev = hostEnv?.IsDevelopment() ?? false;

            var discoveryGroup = app.MapGroup("/.well-known");
            if (!gatewayOptions.Mcp.AllowAnonymousDiscovery && !isDev)
            {
                discoveryGroup.RequireAuthorization();
            }
            else
            {
                discoveryGroup.AllowAnonymous();
            }

            // RFC 9728: Root Protected Resource Metadata Fallback -> redirects to /mcp
            discoveryGroup.MapGet("/oauth-protected-resource", (HttpContext context) =>
            {
                var target = $"{context.Request.PathBase}/.well-known/oauth-protected-resource{mcpBasePath}";
                return Results.Redirect(target, permanent: false);
            });

            // RFC 8414: Authorization Server Discovery
            discoveryGroup.MapGet("/oauth-authorization-server", () =>
            {
                var servers = Autheris.Api.Mcp.GatewayMcpOAuth.AuthorizationServers(gatewayOptions);
                if (servers.Count == 0)
                {
                    return Results.NotFound();
                }
                return Results.Ok(new
                {
                    authorization_servers = servers,
                    issuer = servers.FirstOrDefault()
                });
            });
        }

        return app;
    }

    /// <summary>
    /// SEC H-16: Resolves subject (<c>GetUserSid()</c>), tenant (<c>context.Items</c>) and authorization attributes
    /// of the current request.
    /// </summary>
    internal static McpCaller ResolveCaller(HttpContext context, bool allowOpenMcp)
    {
        var principal = context.User;
        var isAuthenticated = principal.Identity?.IsAuthenticated == true;
        var securityContext = EndpointSecurity.GetSecurityContext(context);

        var principalId = (isAuthenticated
                ? principal.FindFirst("client_id")?.Value
                  ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? principal.FindFirst("sub")?.Value
                  ?? principal.FindFirst("appid")?.Value
                  ?? principal.Identity?.Name
                : null)
            ?? (allowOpenMcp ? "anonymous-ai-agent" : "unknown-agent");

        var tenantId = (securityContext?.TenantId ?? EndpointSecurity.GetRequestTenant(context)).Value;
        var userSid = isAuthenticated ? (securityContext != null ? securityContext.UserSid.Value : principal.GetUserSid()?.Value) : null;
        var roles = isAuthenticated
            ? (securityContext != null ? securityContext.TenantRoles.Concat(securityContext.ClusterRoles).ToList() : principal.GetUserRoles().ToList())
            : new List<string>();
        var groupSids = isAuthenticated
            ? (securityContext != null ? securityContext.GroupSids.Select(s => s.Value).ToList() : principal.GetGroupSids().Select(s => s.Value).ToList())
            : new List<string>();
        var clientIp = securityContext?.ClientIp?.ToString()
                        ?? (context.RequestServices?.GetService<Autheris.Application.Interfaces.IClientIpResolver>()?.ResolveClientIp()
                            ?? context.Connection.RemoteIpAddress)?.ToString();

        var isReadOnly = principal.IsReadOnly();
        var additionalClaims = isAuthenticated
            ? principal.Claims
                .Where(c => c.Type != ClaimTypes.NameIdentifier &&
                            c.Type != "sub" &&
                            c.Type != "tenant_id" &&
                            c.Type != ClaimTypes.PrimarySid &&
                            c.Type != ClaimTypes.Role &&
                            c.Type != ClaimTypes.GroupSid &&
                            c.Type != Autheris.Domain.Security.TokenAccessScope.ClaimType)
                .GroupBy(c => c.Type, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal)
            : null;

        return new McpCaller(principalId, userSid, tenantId, roles, groupSids, clientIp, isReadOnly, additionalClaims);
    }
}
