namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Audit;
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

        var mcpBasePath = Autheris.Api.Security.McpPathResolver.Resolve(gatewayOptions);

        // Streamable HTTP on the official MCP SDK (GatewayMcpServer); stateless, so there are no session endpoints.
        var endpoint = app.MapMcp(mcpBasePath).WithAudit(AuditLevel.Full, AuditEventTypes.TableQuery);

        // SEC M-09: hard body limit for JSON-RPC messages and MCP >= 2025-06-18 batch rejection.
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

                // Check for JSON-RPC batch arrays (MCP >= 2025-06-18 specification removal of batching)
                if (HttpMethods.IsPost(context.Request.Method))
                {
                    context.Request.EnableBuffering();
                    byte[] buffer = new byte[512];
                    int bytesRead = await context.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), context.RequestAborted).ConfigureAwait(false);
                    context.Request.Body.Position = 0;

                    int startIndex = 0;
                    if (bytesRead >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                    {
                        startIndex = 3;
                    }

                    int firstChar = -1;
                    for (int i = startIndex; i < bytesRead; i++)
                    {
                        char c = (char)buffer[i];
                        if (!char.IsWhiteSpace(c))
                        {
                            firstChar = c;
                            break;
                        }
                    }

                    if (firstChar == '[')
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        context.Response.ContentType = "application/json";
                        var batchError = new
                        {
                            jsonrpc = "2.0",
                            id = (object?)null,
                            error = new
                            {
                                code = -32600,
                                message = "JSON-RPC batching is not supported (MCP \u2265 2025-06-18)"
                            }
                        };
                        await context.Response.WriteAsJsonAsync(batchError, context.RequestAborted).ConfigureAwait(false);
                        return;
                    }
                }

                await inner(context).ConfigureAwait(false);
            };
        });

        var hostEnv = env ?? app.ServiceProvider?.GetService<IHostEnvironment>();
        bool isDev = hostEnv?.IsDevelopment() ?? false;

        // Developer CORS policy is scoped strictly to the /mcp endpoint
        if (isDev && gatewayOptions.Mcp.EnableDeveloperCors)
        {
            endpoint.RequireCors("McpDeveloperCors");
        }

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

        if (GatewayMcpOAuth.CanDiscover(gatewayOptions))
        {
            // RFC 9728: Root Protected Resource Metadata Fallback -> redirects to /mcp
            // Always anonymously accessible whenever MCP-OAuth is configured (even outside Development).
            app.MapGet("/.well-known/oauth-protected-resource", (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "public, max-age=3600";
                var target = $"{context.Request.PathBase}/.well-known/oauth-protected-resource{mcpBasePath}";
                return Results.Redirect(target, permanent: false);
            })
            .AllowAnonymous()
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        }

        // Variant A (RFC 8414) & OIDC Discovery: Autheris is a Protected Resource Gateway, not an AS or OIDC Provider.
        // Explicitly return 404 to anonymous clients so FallbackPolicy does not return 401 Unauthorized.
        app.MapGet("/.well-known/oauth-authorization-server", () => Results.NotFound())
            .AllowAnonymous()
            .WithAuditExemption("Public 404 RFC 8414 AS Discovery stub");
        app.MapGet("/.well-known/openid-configuration", () => Results.NotFound())
            .AllowAnonymous()
            .WithAuditExemption("Public 404 OIDC Discovery stub");

        return app;
    }

    /// <summary>
    /// SEC H-16: Resolves subject (<c>GetUserSid()</c>), tenant (<c>context.Items</c>) and authorization attributes
    /// of the current request.
    /// </summary>
    private static bool IsAppOnlyIdentity(ClaimsPrincipal principal, string userSid) =>
        string.Equals(principal.FindFirst("idtyp")?.Value, "app", StringComparison.OrdinalIgnoreCase) ||
        new[] { "client_id", "appid", "azp" }.Any(t =>
            string.Equals(principal.FindFirst(t)?.Value, userSid, StringComparison.OrdinalIgnoreCase));

    // Without a security context the raw role claims are used. The session principal is rebuilt with the "MCP" authentication type, which
    // loses the ForwardAuth marker that ClusterAdminPolicy needs, so a ClusterAdmin claim of a non-canonical identity (forward-auth proxy
    // headers) is dropped here, exactly as SecurityContextFactory partitions the roles.
    private static List<string> RolesWithoutUntrustedClusterAdmin(ClaimsPrincipal principal)
    {
        var canonicalClusterAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        return principal.GetUserRoles()
            .Where(r => canonicalClusterAdmin || !ClusterAdminPolicy.IsClusterAdminRole(r))
            .ToList();
    }

    internal static McpCaller ResolveCaller(HttpContext context, bool allowOpenMcp, CallerAttributeOptions? attributeOptions = null)
    {
        var principal = context.User;
        var isAuthenticated = principal.Identity?.IsAuthenticated == true;
        var securityContext = EndpointSecurity.GetSecurityContext(context);

        var principalId = (isAuthenticated
                ? principal.FindFirst("client_id")?.Value
                  ?? principal.FindFirst("appid")?.Value
                  ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? principal.FindFirst("sub")?.Value
                  ?? principal.FindFirst("oid")?.Value
                  ?? principal.FindFirst(ClaimTypes.PrimarySid)?.Value
                  ?? principal.Identity?.Name
                : null)
            ?? (allowOpenMcp ? "anonymous-ai-agent" : "unknown-agent");

        var tenantId = (securityContext?.TenantId ?? EndpointSecurity.GetRequestTenant(context)).Value;
        var userSid = isAuthenticated ? (securityContext != null ? securityContext.UserSid.Value : principal.GetUserSid()?.Value) : null;
        // SR-M-08: a token without a user identity (app-only; the "user" SID resolved to the client id) carries no user SID, so the
        // session principal stays an application identity and no user-level consent applies.
        if (userSid != null && isAuthenticated && IsAppOnlyIdentity(principal, userSid))
        {
            userSid = null;
        }

        var roles = isAuthenticated
            ? (securityContext != null ? securityContext.TenantRoles.Concat(securityContext.ClusterRoles).ToList() : RolesWithoutUntrustedClusterAdmin(principal))
            : new List<string>();
        // SR-M-08: group SIDs are user-level grants; an app-only identity (no user SID) carries none on any MCP path.
        var groupSids = isAuthenticated && !string.IsNullOrWhiteSpace(userSid)
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
                            c.Type != Autheris.Domain.Security.TokenAccessScope.ClaimType &&
                            c.Type != CallerAttributeSchemeGate.MarkerClaimType &&
                            // SR-M-08: raw role/group claims ("roles", "groups", "wids", ...) stay out; the session carries the vetted ones.
                            Autheris.Application.Mcp.Services.McpProtocolHandler.IsAllowedAdditionalClaimType(c.Type))
                .GroupBy(c => c.Type, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal)
            : null;
        if (additionalClaims is not null && CallerAttributeSchemeGate.HasAllowedOriginalScheme(principal, attributeOptions ?? new CallerAttributeOptions()))
        {
            // BC-129-01: only the gateway sets this marker, and only for an original identity of an allowed scheme.
            additionalClaims[CallerAttributeSchemeGate.MarkerClaimType] = "1";
        }

        return new McpCaller(principalId, userSid, tenantId, roles, groupSids, clientIp, isReadOnly, additionalClaims);
    }
}
