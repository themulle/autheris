namespace Autheris.Api.Middleware;

using Autheris.Api.Errors;
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
            bool revoked;
            try
            {
                revoked = await revocationService.IsRevokedAsync(context.User, context.RequestAborted).ConfigureAwait(false);
            }
            catch (TokenRevocationUnavailableException)
            {
                // SR-L-10: fail closed. The revocation status is unknown, so the pipeline must not run.
                context.Response.Headers.RetryAfter = "5";
                await ApiProblems.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.TokenRevocationUnavailable, "Token revocation status is temporarily unavailable.", context.RequestAborted).ConfigureAwait(false);
                return;
            }

            if (revoked)
            {
                await ApiProblems.WriteAsync(context, StatusCodes.Status401Unauthorized, ApiErrorCodes.TokenRevoked, "Authentication token has been revoked.", context.RequestAborted).ConfigureAwait(false);
                return;
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}
