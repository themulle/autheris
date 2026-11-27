namespace Autheris.Api.Endpoints;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Common.Quota;
using Autheris.Domain.Audit;
using Autheris.Domain.Options.DataSources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>The effective query limits of the calling token (<c>GET /api/v1/me/limits</c>): only the caller's own values, each with its source.</summary>
public static class CallerLimitsEndpoints
{
    public static IEndpointRouteBuilder MapCallerLimitsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/me/limits", HandleGetLimitsAsync)
            .WithName("GetMyQueryLimits")
            .WithSummary("Effective query limits of the calling token, each with its source (installation, tier or scope)")
            .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        return app;
    }

    internal static async Task<IResult> HandleGetLimitsAsync(HttpContext httpContext, ICallerQueryLimitsService limits, CancellationToken ct)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(EndpointSecurity.GetCallerIdentity(httpContext)))
        {
            return ApiProblems.Problem(StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized, "Authentication required.", httpContext, "Unauthorized");
        }

        var apiKey = httpContext.Request.Headers.TryGetValue("X-API-Key", out var key) ? key.ToString() : null;
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();
        var result = await limits.ResolveAsync(httpContext.User, apiKey, clientIp, ct).ConfigureAwait(false);
        return Results.Ok(new { queryLimits = result });
    }
}
