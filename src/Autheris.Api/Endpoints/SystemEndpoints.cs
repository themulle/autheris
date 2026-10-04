namespace Autheris.Api.Endpoints;

using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Observability;
using Autheris.Application.ResourceGroups;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        // F-API-07: Canonical System Metadata & Monitoring Schema ($system / __gateway)
        app.MapGet("/api/governance/system/metrics", async (
            HttpContext context,
            IGatewaySystemMetricsService metricsService,
            IOptions<GatewayOptions> options,
            CancellationToken ct) =>
        {
            var sysOpts = options.Value.SystemMetrics;
            if (!sysOpts.Enabled || !sysOpts.ExposeRestEndpoints)
            {
                return Results.NotFound(new { error = "System metrics endpoints are disabled." });
            }

            if (!IsAuthorized(context.User, sysOpts.AllowedRoles))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var metrics = await metricsService.CollectSystemMetricsAsync(ct).ConfigureAwait(false);
            return Results.Ok(metrics);
        }).RequireAuthorization();

        app.MapGet("/api/governance/system/health", async (
            HttpContext context,
            IGatewaySystemMetricsService metricsService,
            IOptions<GatewayOptions> options,
            CancellationToken ct) =>
        {
            var sysOpts = options.Value.SystemMetrics;
            if (!sysOpts.Enabled || !sysOpts.ExposeRestEndpoints)
            {
                return Results.NotFound(new { error = "System metrics endpoints are disabled." });
            }

            if (!IsAuthorized(context.User, sysOpts.AllowedRoles))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var metrics = await metricsService.CollectSystemMetricsAsync(ct).ConfigureAwait(false);
            var isAllHealthy = metrics.Components.All(c => c.Status == "Healthy");

            return Results.Json(new
            {
                status = isAllHealthy ? "Healthy" : "Degraded",
                version = metrics.Version,
                uptime = metrics.Uptime,
                components = metrics.Components
            }, statusCode: isAllHealthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).RequireAuthorization();

        app.MapGet("/api/governance/system/resource-groups", (
            HttpContext context,
            IResourceGroupManager resourceGroupManager,
            IOptions<GatewayOptions> options) =>
        {
            var sysOpts = options.Value.SystemMetrics;
            if (!sysOpts.Enabled || !sysOpts.ExposeRestEndpoints)
            {
                return Results.NotFound(new { error = "System metrics endpoints are disabled." });
            }

            if (!IsAuthorized(context.User, sysOpts.AllowedRoles))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var metrics = resourceGroupManager.GetMetrics();
            return Results.Ok(metrics);
        }).RequireAuthorization();

        return app;
    }

    private static bool IsAuthorized(ClaimsPrincipal user, IReadOnlyList<string>? allowedRoles)
    {
        if (allowedRoles == null || allowedRoles.Count == 0)
        {
            return user.IsInRole("GovernanceAdmin") || user.IsInRole("ClusterAdmin") || user.IsInRole("SecurityAdmin");
        }

        return allowedRoles.Any(user.IsInRole);
    }
}
