namespace Autheris.Api.Middleware;

using System;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Translates <see cref="GovernanceStoreNotReadyException"/> into a fail-closed 503 Service Unavailable ProblemDetails response (WP-ARC-C-04).
/// </summary>
public sealed class GovernanceReadinessGateMiddleware
{
    private readonly RequestDelegate _next;

    public GovernanceReadinessGateMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (GovernanceStoreNotReadyException) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            var problem = ApiProblems.CreateProblemDetails(
                StatusCodes.Status503ServiceUnavailable,
                ApiErrorCodes.ServiceUnavailable,
                "The governance store is not ready to serve requests yet.",
                context);
            await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted)
                .ConfigureAwait(false);
        }
    }
}
