namespace Autheris.Api.Middleware;

using System;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Microsoft.AspNetCore.Http;

/// <summary>
/// K-K01: Universal token revocation middleware running immediately after Authentication.
/// Enforces instantaneous revocation across all HTTP endpoints (GraphQL, WebSQL, SQL-Endpoints, OData, MCP).
/// </summary>
public sealed class TokenRevocationMiddleware
{
    private readonly RequestDelegate _next;

    public TokenRevocationMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext context, ITokenRevocationService? revocationService)
    {
        if (context.User?.Identity?.IsAuthenticated == true && revocationService != null)
        {
            if (await revocationService.IsRevokedAsync(context.User, context.RequestAborted).ConfigureAwait(false))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    errors = new[]
                    {
                        new
                        {
                            message = "Authentication token has been revoked.",
                            extensions = new { code = "TOKEN_REVOKED" }
                        }
                    }
                }), context.RequestAborted).ConfigureAwait(false);
                return;
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}
