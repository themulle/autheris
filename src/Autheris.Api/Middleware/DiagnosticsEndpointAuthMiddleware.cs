namespace Autheris.Api.Middleware;

using System;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Security;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Guards diagnostics and operational scrape routes when accessed outside standard endpoint authorization (WP-ARC-C-04).
/// </summary>
public sealed class DiagnosticsEndpointAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly PathString _diagnosticsPrefix;

    public DiagnosticsEndpointAuthMiddleware(RequestDelegate next)
        : this(next, new PathString("/api/diagnostics"))
    {
    }

    public DiagnosticsEndpointAuthMiddleware(RequestDelegate next, PathString diagnosticsPrefix)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _diagnosticsPrefix = diagnosticsPrefix;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_diagnosticsPrefix.HasValue &&
            context.Request.Path.StartsWithSegments(_diagnosticsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (context.User?.Identity?.IsAuthenticated != true)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                var problem = ApiProblems.CreateProblemDetails(
                    StatusCodes.Status401Unauthorized,
                    ApiErrorCodes.Unauthorized,
                    "Authentication is required to access diagnostics endpoints.",
                    context);
                await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            }

            if (!GatewayPolicies.HasAnyRole(context.User, GatewayPolicies.OperatorOrAboveRoles))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                var problem = ApiProblems.CreateProblemDetails(
                    StatusCodes.Status403Forbidden,
                    ApiErrorCodes.AccessDenied,
                    "Operator or administrator privileges are required to access diagnostics endpoints.",
                    context);
                await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}
