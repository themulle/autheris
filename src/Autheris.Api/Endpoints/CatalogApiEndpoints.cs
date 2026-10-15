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
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
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

        // B-15: PUT /api/v1/catalog/datasets/{id}/state
        group.MapPut("/datasets/{id}/state", SetDatasetStateHandler)
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApplied);

        // Issue #94: PUT /api/v1/catalog/datasets/{id}/keys (and /api/catalog/datasets/{id}/keys)
        group.MapPut("/datasets/{id}/keys", SetDatasetKeysHandler)
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        app.MapPut("/api/catalog/datasets/{id}/keys", SetDatasetKeysHandler)
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        return app;
    }

    internal static async Task<IResult> SetDatasetKeysHandler(
        string id,
        SetDatasetKeysDto? request,
        Autheris.Application.Interfaces.ITableMetadataRepository metadataRepository,
        CancellationToken ct)
    {
        if (request == null)
        {
            return Results.BadRequest(new { error = "Key definition payload is required." });
        }

        TableMetadata? existing = null;
        if (TableIdentifier.TryParse(id.Replace('/', '.'), out var tableId))
        {
            existing = await metadataRepository.GetTableMetadataAsync(tableId, ct);
        }

        if (existing == null && Guid.TryParse(id, out var tableGuid))
        {
            var allTables = await metadataRepository.GetAllTablesAsync(ct);
            existing = System.Linq.Enumerable.FirstOrDefault(allTables, t => t.Table.Id == tableGuid);
        }

        if (existing == null)
        {
            return Results.NotFound(new { error = $"Dataset '{id}' was not found." });
        }

        var pkColumns = request.PrimaryKey ?? request.PrimaryKeyColumns ?? Array.Empty<string>();
        var indexes = request.Indexes != null
            ? System.Linq.Enumerable.Select(
                request.Indexes,
                i => new TableIndexDefinition(
                    i?.Name ?? string.Empty,
                    i?.Columns ?? Array.Empty<string>(),
                    i?.IsUnique ?? false)).ToList()
            : (System.Collections.Generic.IReadOnlyList<TableIndexDefinition>)Array.Empty<TableIndexDefinition>();

        TableMetadata validated;
        try
        {
            validated = TableKeyDefinitionValidator.Validate(existing with
            {
                PrimaryKeyColumns = pkColumns,
                Indexes = indexes,
                ReplaceKeyDefinition = true
            });
        }
        catch (TableDefinitionException)
        {
            return Results.BadRequest(new { error = "Invalid table key or index definition." });
        }

        var updated = await metadataRepository.UpsertTableMetadataAsync(
            validated with { ReplaceKeyDefinition = true },
            ct);

        return Results.Ok(new
        {
            datasetId = updated.Identifier.ToString(),
            primaryKey = updated.PrimaryKeyColumns,
            indexes = updated.Indexes
        });
    }

    internal static async Task<IResult> SetDatasetStateHandler(
        string id,
        AdminSetDatasetStateDto? request,
        Autheris.Application.Governance.Interfaces.IAccessPlanningService accessPlanningService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.State))
        {
            return Results.BadRequest(new { error = "State is required." });
        }

        var adminSid = httpContext.User.GetUserSid()?.Value ?? httpContext.User.Identity?.Name ?? "system-admin";
        var planningRequest = new AdminSetDatasetStateRequest(id, request.State, request.Reason);
        var result = await accessPlanningService.SetDatasetStateAsync(planningRequest, adminSid, EndpointSecurity.GetRequestTenant(httpContext).Value, ct);
        if (!result.Success)
        {
            return Results.NotFound(new { error = result.Message });
        }

        return Results.Ok(result);
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
        try
        {
            var result = await discoveryService.RegisterDatasourceAsync(request, context, ct);
            return Results.Ok(result);
        }
        catch (ArgumentException ex) when (ex.ParamName == "secretRef")
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static RequestContext CreateRequestContext(HttpContext httpContext)
    {
        var tenant = EndpointSecurity.GetRequestTenant(httpContext);
        var sid = httpContext.User.GetUserSid() ?? new Sid("anonymous");
        var roles = httpContext.User.GetUserRoles();
        return new RequestContext(tenant, sid, roles, httpContext.TraceIdentifier, httpContext.User);
    }
}

public sealed record AdminSetDatasetStateDto(string State, string? Reason = null);

public sealed record DatasetIndexDto(
    string Name,
    System.Collections.Generic.IReadOnlyList<string> Columns,
    bool IsUnique = false);

public sealed record SetDatasetKeysDto(
    System.Collections.Generic.IReadOnlyList<string>? PrimaryKey = null,
    System.Collections.Generic.IReadOnlyList<string>? PrimaryKeyColumns = null,
    System.Collections.Generic.IReadOnlyList<DatasetIndexDto>? Indexes = null);

