namespace Autheris.Api.Endpoints;

using Autheris.Api.Errors;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Observability;
using Autheris.Application.ResourceGroups;
using Autheris.Domain.Audit;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Catalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
                return ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "System metrics endpoints are disabled.");
            }

            if (!IsAuthorized(context.User, sysOpts.GetEffectiveAllowedRoles()))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var metrics = await metricsService.CollectSystemMetricsAsync(ct).ConfigureAwait(false);
            return Results.Ok(metrics);
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapGet("/api/governance/system/health", async (
            HttpContext context,
            IGatewaySystemMetricsService metricsService,
            IOptions<GatewayOptions> options,
            [FromServices] ICatalogSchemaDiagnostics? catalogDiagnostics,
            CancellationToken ct) =>
        {
            var sysOpts = options.Value.SystemMetrics;
            if (!sysOpts.Enabled || !sysOpts.ExposeRestEndpoints)
            {
                return ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "System metrics endpoints are disabled.");
            }

            if (!IsAuthorized(context.User, sysOpts.GetEffectiveAllowedRoles()))
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
                components = metrics.Components,
                graphQlCatalog = BuildCatalogDiagnostics(catalogDiagnostics, context.User)
            }, statusCode: isAllHealthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapGet("/api/governance/system/resource-groups", (
            HttpContext context,
            IResourceGroupManager resourceGroupManager,
            IOptions<GatewayOptions> options) =>
        {
            var sysOpts = options.Value.SystemMetrics;
            if (!sysOpts.Enabled || !sysOpts.ExposeRestEndpoints)
            {
                return ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "System metrics endpoints are disabled.");
            }

            if (!IsAuthorized(context.User, sysOpts.GetEffectiveAllowedRoles()))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var metrics = resourceGroupManager.GetMetrics();
            return Results.Ok(metrics);
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        return app;
    }

    /// <summary>Cap on the table names listed in the health payload (BUG #157, SEC-BUG157-03).</summary>
    internal const int MaxListedOmittedTables = 100;

    /// <summary>
    /// BUG #157: GraphQL catalog omissions for the health payload. Table names are catalog metadata and are listed only for
    /// GovernanceAdmin; every other authorised caller gets the count (SEC-BUG157-02). Null when no diagnostics are registered, which
    /// omits the property. Entries carry only the qualified table name and the reason (SEC-BUG157-03). Never influences the status.
    /// </summary>
    private static object? BuildCatalogDiagnostics(ICatalogSchemaDiagnostics? diagnostics, ClaimsPrincipal user)
    {
        if (diagnostics == null)
        {
            return null;
        }

        var omitted = diagnostics.OmittedTables;
        if (!GatewayPolicies.HasRole(user, GatewayRole.GovernanceAdmin))
        {
            return new { omittedTableCount = omitted.Count };
        }

        var listed = omitted
            .Take(MaxListedOmittedTables)
            .Select(o => new { table = $"{o.Table.Domain}.{o.Table.Schema}.{o.Table.TableName}", reason = o.Reason.ToString() })
            .ToArray();
        return new
        {
            omittedTableCount = omitted.Count,
            omittedTables = listed,
            omittedTablesTruncated = omitted.Count > listed.Length
        };
    }

    private static bool IsAuthorized(ClaimsPrincipal user, IReadOnlyList<string>? allowedRoles)
    {
        if (allowedRoles == null || allowedRoles.Count == 0)
        {
            return GatewayPolicies.HasRole(user, GatewayRole.GovernanceAdmin);
        }

        return GatewayPolicies.HasAnyRole(user, allowedRoles);
    }
}
