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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

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
        string? ClientIp = null);

    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app, GatewayOptions gatewayOptions)
    {
        if (!gatewayOptions.Mcp.Enabled)
        {
            return app;
        }

        var mcpBasePath = string.IsNullOrWhiteSpace(gatewayOptions.Mcp.EndpointPath)
            ? "/mcp"
            : gatewayOptions.Mcp.EndpointPath.TrimEnd('/');

        // SEC H-02: OpenSchema no longer opens MCP. Only the explicit (production-blocked) MCP auth bypass does.
        var allowOpenMcp = gatewayOptions.IsMcpAuthBypassed;

        // 1. SSE Connection Handshake
        var sseEndpoint = app.MapGet($"{mcpBasePath}/sse", async (
            IMcpProtocolHandler mcpHandler,
            IMcpSessionStore sessionStore,
            HttpContext context) =>
        {
            var principal = context.User;
            var isAuthenticated = principal.Identity?.IsAuthenticated == true;

            if (!isAuthenticated && !allowOpenMcp)
            {
                return Results.Unauthorized();
            }

            var caller = ResolveCaller(context, allowOpenMcp);

            McpSessionContext session;
            try
            {
                session = mcpHandler.CreateSession(caller.PrincipalId, caller.TenantId, caller.UserSid, caller.Roles, caller.GroupSids, caller.ClientIp);
            }
            catch (McpSessionLimitExceededException)
            {
                return SessionLimitResult();
            }

            // MCP-3: tool progress events, the endpoint event and keep-alive pings write to the same response from
            // different tasks; HTTP response writes are not thread-safe, so every write goes through one gate.
            using var writeGate = new SemaphoreSlim(1, 1);
            async Task WriteSseAsync(string text)
            {
                await writeGate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                try
                {
                    await context.Response.WriteAsync(text, context.RequestAborted).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                }
                finally
                {
                    writeGate.Release();
                }
            }

            sessionStore.RegisterSseSender(session.SessionId, async (evt, data) =>
            {
                var cleanEvt = string.IsNullOrWhiteSpace(evt) ? "message" : Regex.Replace(evt, @"[\r\n]", string.Empty);
                var normalizedData = (data ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
                var lines = normalizedData.Split('\n');
                var sb = new System.Text.StringBuilder();
                sb.Append("event: ").Append(cleanEvt).Append('\n');
                foreach (var line in lines)
                {
                    sb.Append("data: ").Append(line).Append('\n');
                }
                sb.Append('\n');
                await WriteSseAsync(sb.ToString()).ConfigureAwait(false);
            });

            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";
            context.Response.Headers["Mcp-Session-Id"] = session.SessionId;

            // The query parameter is kept for SSE client compatibility; the session is bound to the caller (SEC H-16),
            // so a leaked id cannot be used by anybody else. Clients should prefer the Mcp-Session-Id header.
            var messageUri = $"{mcpBasePath}/message?sessionId={session.SessionId}";
            await WriteSseAsync($"event: endpoint\r\ndata: {messageUri}\r\n\r\n").ConfigureAwait(false);

            try
            {
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    await Task.Delay(15000, context.RequestAborted).ConfigureAwait(false);
                    await WriteSseAsync(": ping\r\n\r\n").ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal client disconnect
            }
            finally
            {
                mcpHandler.RemoveSession(session.SessionId);
            }

            return Results.Empty;
        });

        ApplyAuthorization(sseEndpoint, allowOpenMcp);

        // 2. JSON-RPC Message Receiver
        var messageEndpoint = app.MapPost($"{mcpBasePath}/message", async (
            IMcpProtocolHandler mcpHandler,
            IMcpSessionStore sessionStore,
            HttpContext context) =>
        {
            if (context.User.Identity?.IsAuthenticated != true && !allowOpenMcp)
            {
                return Results.Unauthorized();
            }

            var sessionId = GetSessionIdFromRequest(context.Request, preferHeader: true);

            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Results.BadRequest(new { error = "Missing 'Mcp-Session-Id' header (or 'sessionId' query parameter)." });
            }

            var session = await mcpHandler.GetSessionAsync(sessionId, context.RequestAborted);
            if (session == null)
            {
                return Results.NotFound(new { error = "Invalid or expired MCP session." });
            }

            // SEC H-16: The session is bound to subject + tenant and checked on every request (independent of OpenSchema).
            var caller = ResolveCaller(context, allowOpenMcp);
            if (!McpSessionBinding.IsOwnedBy(session, caller.UserSid, caller.TenantId))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var (payload, tooLarge) = await EndpointSecurity.TryReadBodyAsync(
                context.Request,
                MaxMcpMessageBytes,
                "MCP message exceeds maximum allowed size (1 MB).",
                context.RequestAborted).ConfigureAwait(false);
            if (tooLarge != null)
            {
                return tooLarge;
            }

            if (string.IsNullOrWhiteSpace(payload))
            {
                return Results.BadRequest(new { error = "Empty JSON-RPC payload." });
            }

            // SEC H-16: Roles/groups are taken from the current principal for every request.
            sessionStore.RefreshPrincipalContext(session.SessionId, caller.Roles, caller.GroupSids, caller.ClientIp);

            var responseJson = await mcpHandler.HandleMessageAsync(session.SessionId, payload, context.RequestAborted).ConfigureAwait(false);

            return Results.Content(responseJson, "application/json; charset=utf-8");
        });

        ApplyAuthorization(messageEndpoint, allowOpenMcp);

        // 2b. Streamable HTTP Transport (MCP 2024-11-05 Specification)
        var streamableHttpEndpoint = app.MapPost(mcpBasePath, async (
            IMcpProtocolHandler mcpHandler,
            IMcpSessionStore sessionStore,
            HttpContext context) =>
        {
            if (context.User.Identity?.IsAuthenticated != true && !allowOpenMcp)
            {
                return Results.Unauthorized();
            }

            var caller = ResolveCaller(context, allowOpenMcp);
            var sessionId = GetSessionIdFromRequest(context.Request, preferHeader: true);

            McpSessionContext? session = null;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                session = await mcpHandler.GetSessionAsync(sessionId, context.RequestAborted);

                // SEC H-16: Always enforce the subject + tenant binding.
                if (session != null && !McpSessionBinding.IsOwnedBy(session, caller.UserSid, caller.TenantId))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
            }

            // SEC M-09: Read (bounded) before creating a session, so oversized requests cannot allocate sessions.
            var (payload, tooLarge) = await EndpointSecurity.TryReadBodyAsync(
                context.Request,
                MaxMcpMessageBytes,
                "MCP message exceeds maximum allowed size (1 MB).",
                context.RequestAborted).ConfigureAwait(false);
            if (tooLarge != null)
            {
                return tooLarge;
            }

            if (string.IsNullOrWhiteSpace(payload))
            {
                return Results.BadRequest(new { error = "Empty JSON-RPC payload." });
            }

            if (session == null)
            {
                try
                {
                    session = mcpHandler.CreateSession(caller.PrincipalId, caller.TenantId, caller.UserSid, caller.Roles, caller.GroupSids, caller.ClientIp);
                }
                catch (McpSessionLimitExceededException)
                {
                    return SessionLimitResult();
                }
            }
            else
            {
                sessionStore.RefreshPrincipalContext(session.SessionId, caller.Roles, caller.GroupSids, caller.ClientIp);
            }

            context.Response.Headers["X-MCP-Session-Id"] = session.SessionId;
            context.Response.Headers["Mcp-Session-Id"] = session.SessionId;

            var responseJson = await mcpHandler.HandleMessageAsync(session.SessionId, payload, context.RequestAborted).ConfigureAwait(false);
            return Results.Content(responseJson, "application/json; charset=utf-8");
        });

        ApplyAuthorization(streamableHttpEndpoint, allowOpenMcp);

        // 3. Session Teardown
        var sessionEndpoint = app.MapDelete($"{mcpBasePath}/session/{{id}}", async (
            string id,
            IMcpProtocolHandler mcpHandler,
            HttpContext context) =>
        {
            if (context.User.Identity?.IsAuthenticated != true && !allowOpenMcp)
            {
                return Results.Unauthorized();
            }

            var session = await mcpHandler.GetSessionAsync(id, context.RequestAborted);
            if (session == null)
            {
                return Results.NotFound(new { error = "Session not found." });
            }

            var caller = ResolveCaller(context, allowOpenMcp);
            if (!McpSessionBinding.IsOwnedBy(session, caller.UserSid, caller.TenantId))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var removed = mcpHandler.RemoveSession(id);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        ApplyAuthorization(sessionEndpoint, allowOpenMcp);

        return app;
    }

    private static void ApplyAuthorization(RouteHandlerBuilder endpoint, bool allowOpenMcp)
    {
        if (allowOpenMcp)
        {
            // Explicit opt-out of the fallback policy; only reachable with danger_bypass_mcp_auth (blocked in production).
            endpoint.AllowAnonymous();
        }
        else
        {
            endpoint.RequireAuthorization();
        }
    }

    private static IResult SessionLimitResult()
        => Results.Json(
            new { error = "MCP session limit reached. Close unused sessions or retry later." },
            statusCode: StatusCodes.Status429TooManyRequests);

    /// <summary>
    /// SEC H-16: The session id is preferably taken from the <c>Mcp-Session-Id</c> header; the query parameter is only
    /// accepted for SSE client compatibility (the session binding prevents use of a leaked id by other subjects).
    /// </summary>
    internal static string? GetSessionIdFromRequest(HttpRequest request, bool preferHeader)
    {
        var fromHeader = request.Headers["Mcp-Session-Id"].FirstOrDefault()
            ?? request.Headers["X-MCP-Session-Id"].FirstOrDefault();
        var fromQuery = request.Query["sessionId"].FirstOrDefault();

        var sessionId = preferHeader ? (fromHeader ?? fromQuery) : (fromQuery ?? fromHeader);
        return string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
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

        return new McpCaller(principalId, userSid, tenantId, roles, groupSids, clientIp);
    }
}
