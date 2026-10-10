namespace Autheris.Api.Endpoints;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class CatalogApiEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/catalog");

        group.MapGet("/datasets", async (
            ICatalogDiscoveryService discoveryService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var context = CreateRequestContext(httpContext);
            var datasets = await discoveryService.ListDatasetsAsync(context, ct);
            return Results.Ok(datasets);
        })
        .RequireAuthorization()
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapGet("/datasets/{*datasetId}", async (
            string datasetId,
            ICatalogDiscoveryService discoveryService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (!TableIdentifier.TryParse(datasetId.Replace('/', '.'), out var tableId))
            {
                return Results.BadRequest(new { error = $"Invalid dataset identifier '{datasetId}'." });
            }

            var context = CreateRequestContext(httpContext);
            var detail = await discoveryService.GetDatasetDetailAsync(tableId, context, ct);
            return detail is not null ? Results.Ok(detail) : Results.NotFound();
        })
        .RequireAuthorization()
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapGet("/datasources", async (
            ICatalogDiscoveryService discoveryService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var context = CreateRequestContext(httpContext);
            var datasources = await discoveryService.ListDatasourcesAsync(context, ct);
            return Results.Ok(datasources);
        })
        .RequireAuthorization()
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapPost("/datasources", RegisterDatasourceHandler)
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        // Also expose on /api/governance/datasources (R-54)
        app.MapPost("/api/governance/datasources", RegisterDatasourceHandler)
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        group.MapGet("/search", async (
            string? q,
            string? domain,
            int? limit,
            string? mode,
            ICatalogDiscoveryService discoveryService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var context = CreateRequestContext(httpContext);
            var searchMode = mode?.ToLowerInvariant() switch
            {
                "semantic" => CatalogSearchMode.SemanticVector,
                "keyword" => CatalogSearchMode.KeywordBm25,
                _ => CatalogSearchMode.Hybrid
            };

            var query = new CatalogSearchQuery(
                QueryText: q ?? string.Empty,
                DomainFilter: domain,
                Limit: Math.Clamp(limit ?? 10, 1, 50),
                Mode: searchMode);

            var hits = await discoveryService.SearchCatalogDetailedAsync(query, context, ct);
            return Results.Ok(hits);
        })
        .RequireAuthorization()
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // R-61: GET /api/governance/principals?q=
        app.MapGet("/api/governance/principals", async (
            string? q,
            IPrincipalResolverService resolverService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Results.BadRequest(new { error = "Query parameter 'q' is required." });
            }

            var context = CreateRequestContext(httpContext);
            var results = await resolverService.ResolvePrincipalAsync(q, context, ct);
            return Results.Ok(results);
        })
        .RequireAuthorization()
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // R-55: POST /api/v1/catalog/datasources/{id}/test
        group.MapPost("/datasources/{id}/test", async (
            string id,
            DatasourceTestRequest? request,
            IDatasourceTestingService testingService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var effectiveRequest = request ?? new DatasourceTestRequest();
            var result = await testingService.TestDatasourceAsync(id, effectiveRequest, httpContext.User, ct);
            return Results.Ok(result);
        })
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.DatasourceTested);

        return app;
    }

    private static async Task<IResult> RegisterDatasourceHandler(
        DatasourceRegistrationRequest request,
        ICatalogDiscoveryService discoveryService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Domain))
        {
            return Results.BadRequest(new { error = "Name and Domain are required fields." });
        }

        var context = CreateRequestContext(httpContext);
        var result = await discoveryService.RegisterDatasourceAsync(request, context, ct);
        return Results.Ok(result);
    }

    private static RequestContext CreateRequestContext(HttpContext httpContext)
    {
        var tenant = EndpointSecurity.GetRequestTenant(httpContext);
        var sid = httpContext.User.GetUserSid() ?? new Sid("anonymous");
        var roles = httpContext.User.GetUserRoles();
        return new RequestContext(tenant, sid, roles, httpContext.TraceIdentifier, httpContext.User);
    }
}
