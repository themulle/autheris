namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Localization;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

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
            var lang = httpContext.Request.Query.TryGetValue("lang", out var langVal) ? langVal.ToString() : null;
            var acceptLanguage = httpContext.Request.Headers.AcceptLanguage.ToString();
            var requestedLang = !string.IsNullOrWhiteSpace(lang) ? lang : (!string.IsNullOrWhiteSpace(acceptLanguage) ? acceptLanguage : null);
            var languageNegotiator = httpContext.RequestServices.GetService(typeof(ILanguageNegotiator)) as ILanguageNegotiator ?? new LanguageNegotiator();
            var localizationOptions = (httpContext.RequestServices.GetService(typeof(IOptions<CatalogLocalizationOptions>)) as IOptions<CatalogLocalizationOptions>)?.Value ?? new CatalogLocalizationOptions();
            var targetLang = languageNegotiator.NegotiateLanguage(requestedLang, localizationOptions.SupportedLanguages, localizationOptions.DefaultLanguage);
            httpContext.Response.Headers.Append("Content-Language", targetLang);
            httpContext.Response.Headers.Append("Vary", "Accept-Language");

            var context = CreateRequestContext(httpContext, targetLang);
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

            var lang = httpContext.Request.Query.TryGetValue("lang", out var langVal) ? langVal.ToString() : null;
            var acceptLanguage = httpContext.Request.Headers.AcceptLanguage.ToString();
            var requestedLang = !string.IsNullOrWhiteSpace(lang) ? lang : (!string.IsNullOrWhiteSpace(acceptLanguage) ? acceptLanguage : null);
            var languageNegotiator = httpContext.RequestServices.GetService(typeof(ILanguageNegotiator)) as ILanguageNegotiator ?? new LanguageNegotiator();
            var localizationOptions = (httpContext.RequestServices.GetService(typeof(IOptions<CatalogLocalizationOptions>)) as IOptions<CatalogLocalizationOptions>)?.Value ?? new CatalogLocalizationOptions();
            var targetLang = languageNegotiator.NegotiateLanguage(requestedLang, localizationOptions.SupportedLanguages, localizationOptions.DefaultLanguage);
            httpContext.Response.Headers.Append("Content-Language", targetLang);
            httpContext.Response.Headers.Append("Vary", "Accept-Language");

            var context = CreateRequestContext(httpContext, targetLang);
            var detail = await discoveryService.GetDatasetDetailAsync(tableId, context, ct);
            if (detail is null)
            {
                return Results.NotFound();
            }

            var isBrief = httpContext.Request.Query.TryGetValue("brief", out var briefVal) &&
                          bool.TryParse(briefVal.ToString(), out var b) && b;
            if (isBrief)
            {
                var briefDetail = new
                {
                    dataset = detail.DatasetId,
                    domain = detail.Domain,
                    schema = detail.Schema,
                    table = detail.Table,
                    description = detail.Description,
                    sensitivity = detail.Sensitivity,
                    primaryKeys = detail.PrimaryKeys,
                    columns = detail.Columns.Select(c => new
                    {
                        name = c.Name,
                        type = c.Type,
                        description = c.Description,
                        isPrimaryKey = c.IsPrimaryKey,
                        isSensitive = c.Sensitivity != "Public" && c.Sensitivity != "NORMAL" && !string.IsNullOrEmpty(c.Sensitivity)
                    })
                };
                return Results.Ok(briefDetail);
            }

            return Results.Ok(detail);
        })
        .RequireAuthorization()
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapGet("/domains", async (
            ICatalogDiscoveryService discoveryService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var context = CreateRequestContext(httpContext);
            var datasets = await discoveryService.ListDatasetsAsync(context, ct);
            var domains = datasets
                .GroupBy(d => d.Domain, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { domain = g.Key, tableCount = g.Count() })
                .OrderByDescending(d => d.tableCount)
                .ToList();
            return Results.Ok(new { totalTables = datasets.Count, totalDomains = domains.Count, domains });
        })
        .RequireAuthorization()
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // Data sources disclose endpoint base URLs and authentication state: administrators only. A tenant-scoped GovernanceAdmin
        // is further confined to the tables of its tenant by the service. A GovernanceAdmin without tenant claim in Multi mode is
        // answered with a stable 403 that names the option instead of a misleading empty list.
        group.MapGet("/datasources", async (
            ICatalogDiscoveryService discoveryService,
            IOptions<GatewayOptions> gatewayOptions,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var scope = CallerScopeResolver.Resolve(httpContext.User, gatewayOptions.Value.Tenancy.EffectiveMode);
            if (scope == CallerScope.Claimless && CallerScopeResolver.HoldsGovernanceAdminRole(httpContext.User))
            {
                return Results.Json(
                    new
                    {
                        error = "TENANCY_MODE_CLAIMLESS_ADMIN",
                        message = "The principal has no tenant claim and Gateway:Tenancy:Mode is Multi; single-tenant deployments must set Gateway:Tenancy:Mode=Single."
                    },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var context = CreateRequestContext(httpContext);
            var datasources = await discoveryService.ListDatasourcesAsync(context, ct);
            return Results.Ok(datasources);
        })
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
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
            var lang = httpContext.Request.Query.TryGetValue("lang", out var langVal) ? langVal.ToString() : null;
            var acceptLanguage = httpContext.Request.Headers.AcceptLanguage.ToString();
            var requestedLang = !string.IsNullOrWhiteSpace(lang) ? lang : (!string.IsNullOrWhiteSpace(acceptLanguage) ? acceptLanguage : null);
            var languageNegotiator = httpContext.RequestServices.GetService(typeof(ILanguageNegotiator)) as ILanguageNegotiator ?? new LanguageNegotiator();
            var localizationOptions = (httpContext.RequestServices.GetService(typeof(IOptions<CatalogLocalizationOptions>)) as IOptions<CatalogLocalizationOptions>)?.Value ?? new CatalogLocalizationOptions();
            var targetLang = languageNegotiator.NegotiateLanguage(requestedLang, localizationOptions.SupportedLanguages, localizationOptions.DefaultLanguage);
            httpContext.Response.Headers.Append("Content-Language", targetLang);
            httpContext.Response.Headers.Append("Vary", "Accept-Language");

            var context = CreateRequestContext(httpContext, targetLang);
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
                Mode: searchMode,
                Language: targetLang);

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
            if (string.IsNullOrWhiteSpace(id) || !DatasourceTestingService.IsValidDatasourceId(id))
            {
                return Results.BadRequest(new { error = "Invalid datasource identifier format." });
            }
            try
            {
                var effectiveRequest = request ?? new DatasourceTestRequest();
                var result = await testingService.TestDatasourceAsync(id, effectiveRequest, httpContext.User, ct);
                return Results.Ok(result);
            }
            catch (System.Collections.Generic.KeyNotFoundException)
            {
                return Results.NotFound(new { error = "Datasource was not found in catalog or configuration." });
            }
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
        .WithAudit(AuditLevel.Full, AuditEventTypes.CatalogKeyDefinitionChanged);

        app.MapPut("/api/catalog/datasets/{id}/keys", SetDatasetKeysHandler)
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.CatalogKeyDefinitionChanged);

        // Issue #221: PUT /api/v1/catalog/datasets/{id}/required-filters (and /api/catalog/datasets/{id}/required-filters)
        group.MapPut("/datasets/{id}/required-filters", SetDatasetRequiredFiltersHandler)
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        app.MapPut("/api/catalog/datasets/{id}/required-filters", SetDatasetRequiredFiltersHandler)
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        // Predefined Synthetic Samples
        group.MapPut("/datasets/{id}/synthetic-samples", SetDatasetSyntheticSamplesHandler)
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        app.MapPut("/api/catalog/datasets/{id}/synthetic-samples", SetDatasetSyntheticSamplesHandler)
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        group.MapGet("/datasets/{id}/synthetic-samples", GetDatasetSyntheticSamplesHandler)
        .RequireAuthorization()
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapGet("/api/catalog/datasets/{id}/synthetic-samples", GetDatasetSyntheticSamplesHandler)
        .RequireAuthorization()
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // Issue #111: dataset tags and catalog visibility tier. RequireAuthorization() only: owners must reach the routes, so the
        // decision (admin class, owner or delegate, Full only for administrators) is taken inside DatasetCatalogAdminService.
        MapDatasetCatalogRoutes(group);
        MapDatasetCatalogRoutes(app.MapGroup("/api/catalog"));

        // Issue #107: object level of the row filter key-set cache strategy (same authorization and dataset resolution as /keys).
        group.MapGet("/datasets/{id}/row-filter-strategy", GovernanceEndpoints.GetDatasetRowFilterStrategyAsync)
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapPut("/datasets/{id}/row-filter-strategy", GovernanceEndpoints.SetDatasetRowFilterStrategyAsync)
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.TableRowFilterStrategyChanged);

        group.MapDelete("/datasets/{id}/row-filter-strategy", GovernanceEndpoints.ClearDatasetRowFilterStrategyAsync)
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.TableRowFilterStrategyChanged);

        group.MapPut("/relations/{relationId:guid}/cross-data-source", async (
            Guid relationId,
            SetRelationCrossDataSourceDto request,
            [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Interfaces.ITableRelationRepository relationRepo,
            CancellationToken ct) =>
        {
            var updated = await relationRepo.SetRelationCrossDataSourceOptInAsync(relationId, request.AllowCrossDataSource, ct);
            if (!updated)
            {
                return Results.NotFound(new { error = $"Relation '{relationId}' not found." });
            }
            return Results.Ok(new { relationId, allowCrossDataSource = request.AllowCrossDataSource });
        })
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.CrossSourceOptInChanged);

        return app;
    }

    private static void MapDatasetCatalogRoutes(IEndpointRouteBuilder group)
    {
        group.MapGet("/datasets/{id}/catalog-settings", GetDatasetCatalogSettingsHandler)
        .RequireAuthorization()
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapPut("/datasets/{id}/tags", PutDatasetTagsHandler)
        .RequireAuthorization()
        .WithAudit(AuditLevel.Full, AuditEventTypes.CatalogTagsChanged);

        group.MapPost("/datasets/{id}/tags", PostDatasetTagsHandler)
        .RequireAuthorization()
        .WithAudit(AuditLevel.Full, AuditEventTypes.CatalogTagsChanged);

        group.MapPut("/datasets/{id}/visibility", PutDatasetVisibilityHandler)
        .RequireAuthorization()
        .WithAudit(AuditLevel.Full, AuditEventTypes.CatalogVisibilityChanged);
    }

    internal static async Task<IResult> GetDatasetCatalogSettingsHandler(
        string id,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Governance.Interfaces.IDatasetCatalogAdminService service,
        HttpContext httpContext,
        CancellationToken ct)
        => MapCatalogResult(await service.GetAsync(httpContext.User, id, ct).ConfigureAwait(false));

    internal static async Task<IResult> PutDatasetTagsHandler(
        string id,
        SetDatasetTagsDto? request,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Governance.Interfaces.IDatasetCatalogAdminService service,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var edit = request?.Tags is null
            ? new Autheris.Application.Governance.Interfaces.DatasetCatalogEdit(null, null, null, null) { Malformed = true }
            : new Autheris.Application.Governance.Interfaces.DatasetCatalogEdit(request.Tags, null, null, null);
        return MapCatalogResult(await service.ApplyAsync(httpContext.User, id, edit, "admin", ct).ConfigureAwait(false));
    }

    internal static async Task<IResult> PostDatasetTagsHandler(
        string id,
        ChangeDatasetTagsDto? request,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Governance.Interfaces.IDatasetCatalogAdminService service,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var edit = request is null || (request.Add is null && request.Remove is null)
            ? new Autheris.Application.Governance.Interfaces.DatasetCatalogEdit(null, null, null, null) { Malformed = true }
            : new Autheris.Application.Governance.Interfaces.DatasetCatalogEdit(null, request.Add, request.Remove, null);
        return MapCatalogResult(await service.ApplyAsync(httpContext.User, id, edit, "admin", ct).ConfigureAwait(false));
    }

    internal static async Task<IResult> PutDatasetVisibilityHandler(
        string id,
        SetDatasetVisibilityDto? request,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Governance.Interfaces.IDatasetCatalogAdminService service,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var edit = request?.Visibility is null
            ? new Autheris.Application.Governance.Interfaces.DatasetCatalogEdit(null, null, null, null) { Malformed = true }
            : new Autheris.Application.Governance.Interfaces.DatasetCatalogEdit(null, null, null, request.Visibility);
        return MapCatalogResult(await service.ApplyAsync(httpContext.User, id, edit, "admin", ct).ConfigureAwait(false));
    }

    /// <summary>One mapping for all four routes; NotFound has one fixed body so unknown, inactive and non-manageable datasets cannot be told apart (SEC-111-08).</summary>
    private static IResult MapCatalogResult(Autheris.Application.Governance.Interfaces.DatasetCatalogResult result)
    {
        switch (result.Outcome)
        {
            case Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.Applied:
            case Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.Unchanged:
                var settings = result.Settings!;
                return Results.Ok(new DatasetCatalogSettingsDto(
                    result.Table!.Value.ToString(),
                    [.. settings.Tags.OrderBy(t => t.Tag, StringComparer.Ordinal).Select(t => new DatasetTagDto(t.Tag, t.Source == DatasetTagSource.Dbt ? "dbt" : "admin"))],
                    settings.Visibility.ToString(),
                    result.TierEffective));
            case Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.Forbidden:
                return Results.Json(new { error = "FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);
            case Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.InvalidTag:
                return Results.Json(new { error = "INVALID_TAG" }, statusCode: StatusCodes.Status400BadRequest);
            case Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.TagLimitExceeded:
                return Results.Json(new { error = "TAG_LIMIT_EXCEEDED" }, statusCode: StatusCodes.Status400BadRequest);
            case Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.InvalidVisibility:
                return Results.Json(new { error = "INVALID_VISIBILITY" }, statusCode: StatusCodes.Status400BadRequest);
            case Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.InvalidRequest:
                return Results.Json(new { error = "INVALID_REQUEST" }, statusCode: StatusCodes.Status400BadRequest);
            case Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.Unavailable:
                return Results.Json(new { error = "UNAVAILABLE" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            default:
                return Results.Json(new { error = "NOT_FOUND" }, statusCode: StatusCodes.Status404NotFound);
        }
    }

    private static IResult DatasetKeysNotFound() => Results.NotFound(new { error = "Dataset was not found." });

    /// <summary>Resolves an active dataset by table id or domain.schema.table (shared with the row filter strategy endpoints).</summary>
    private static Task<TableIdentifier?> ResolveActiveDatasetAsync(
        string? id,
        Autheris.Application.Interfaces.ITableMetadataRepository metadataRepository,
        CancellationToken ct)
        => Autheris.Application.Governance.Services.DatasetIdResolver.ResolveActiveAsync(id, metadataRepository, ct);

    internal static async Task<IResult> SetDatasetKeysHandler(
        string id,
        SetDatasetKeysDto? request,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Interfaces.ITableMetadataRepository metadataRepository,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Audit.IAuditWriter auditWriter,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (httpContext.User.GetUserSid() is not { } actorSid)
        {
            // Fail closed: a change without an identifiable actor is never written (no placeholder SID in the HMAC-chained audit).
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (request == null)
        {
            return Results.BadRequest(new { error = "Key definition payload is required." });
        }

        var tableId = await ResolveActiveDatasetAsync(id, metadataRepository, ct).ConfigureAwait(false);
        if (tableId is null)
        {
            return DatasetKeysNotFound();
        }

        var pkColumns = request.PrimaryKey ?? request.PrimaryKeyColumns ?? Array.Empty<string>();
        if (request.Indexes != null && System.Linq.Enumerable.Any(request.Indexes, i => i is { Unique: not null, IsUnique: not null } && i.Unique != i.IsUnique))
        {
            // `unique` is canonical, `isUnique` an accepted alias; contradicting values are refused with the generic message.
            return Results.BadRequest(new { error = "Invalid table key or index definition." });
        }

        var indexes = request.Indexes != null
            ? System.Linq.Enumerable.Select(
                request.Indexes,
                i => new TableIndexDefinition(
                    i?.Name ?? string.Empty,
                    i?.Columns ?? Array.Empty<string>(),
                    i?.Unique ?? i?.IsUnique ?? false)).ToList()
            : (System.Collections.Generic.IReadOnlyList<TableIndexDefinition>)Array.Empty<TableIndexDefinition>();

        // The caps are enforced by the validator inside the repository before anything is allocated per element.
        TableKeyDefinitionChange? change;
        try
        {
            change = await metadataRepository.ReplaceTableKeyDefinitionAsync(tableId.Value, pkColumns, indexes, ct).ConfigureAwait(false);
        }
        catch (TableDefinitionException)
        {
            return Results.BadRequest(new { error = "Invalid table key or index definition." });
        }

        if (change is null)
        {
            return DatasetKeysNotFound();
        }

        // SEC-94-06: a changed definition is traceable in the HMAC-chained audit log (identifiers only, no data). Post-commit: a client
        // disconnect must not drop the record; an unavailable audit store surfaces as 503 through the exception handler (fail closed).
        await auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            TenantId = EndpointSecurity.GetRequestTenant(httpContext),
            EventType = AuditEventTypes.CatalogKeyDefinitionChanged,
            ActorSid = actorSid,
            TargetTable = tableId.Value.ToString(),
            Decision = "ALLOW",
            TraceId = httpContext.TraceIdentifier,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                datasetId = tableId.Value.ToString(),
                before = change.BeforeUnavailable
                    ? (object)new { unavailable = true }
                    : new { primaryKey = change.Before.PrimaryKey, indexes = change.Before.Indexes },
                after = new { primaryKey = change.After.PrimaryKey, indexes = change.After.Indexes }
            })
        }, CancellationToken.None).ConfigureAwait(false);

        return Results.Ok(new
        {
            datasetId = tableId.Value.ToString(),
            primaryKey = change.After.PrimaryKey,
            indexes = change.After.Indexes
        });
    }

    internal static async Task<IResult> SetDatasetRequiredFiltersHandler(
        string id,
        SetDatasetRequiredFiltersDto? request,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Interfaces.ITableMetadataRepository metadataRepository,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Audit.IAuditWriter auditWriter,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var tableId = await ResolveActiveDatasetAsync(id, metadataRepository, ct).ConfigureAwait(false);
        if (tableId is null)
        {
            return DatasetKeysNotFound();
        }

        var meta = await metadataRepository.GetTableMetadataAsync(tableId.Value, ct).ConfigureAwait(false);
        if (meta is null)
        {
            return DatasetKeysNotFound();
        }

        var reqCols = request?.RequiredFilterColumns?.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray()
            ?? ImmutableArray<string>.Empty;

        var reqGroupsList = new List<ImmutableArray<string>>();
        if (request?.RequiredFilterColumnGroups is not null)
        {
            foreach (var g in request.RequiredFilterColumnGroups)
            {
                if (g is not null)
                {
                    var groupCols = g.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
                    if (groupCols.Length > 0)
                    {
                        reqGroupsList.Add(groupCols);
                    }
                }
            }
        }
        var reqGroups = reqGroupsList.ToImmutableArray();

        var colNames = new HashSet<string>(meta.Columns.Select(c => c.ColumnName), StringComparer.OrdinalIgnoreCase);
        foreach (var c in reqCols)
        {
            if (!colNames.Contains(c))
            {
                return Results.BadRequest(new { error = $"Column '{c}' does not exist on table '{tableId.Value}'." });
            }
        }
        foreach (var group in reqGroups)
        {
            foreach (var c in group)
            {
                if (!colNames.Contains(c))
                {
                    return Results.BadRequest(new { error = $"Column '{c}' does not exist on table '{tableId.Value}'." });
                }
            }
        }

        var updatedTable = meta.Table with
        {
            RequiredFilterColumns = reqCols,
            RequiredFilterColumnGroups = reqGroups
        };

        var updatedMeta = meta with { Table = updatedTable };
        await metadataRepository.UpsertTableMetadataAsync(updatedMeta, ct).ConfigureAwait(false);

        var actorSid = httpContext.User.GetUserSid()?.Value ?? httpContext.User.Identity?.Name ?? "system-admin";
        await auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            TenantId = EndpointSecurity.GetRequestTenant(httpContext),
            EventType = AuditEventTypes.AuditConfigChanged,
            ActorSid = actorSid,
            TargetTable = tableId.Value.ToString(),
            Decision = "ALLOW",
            TraceId = httpContext.TraceIdentifier,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                datasetId = tableId.Value.ToString(),
                requiredFilterColumns = reqCols,
                requiredFilterColumnGroups = reqGroups
            })
        }, CancellationToken.None).ConfigureAwait(false);

        return Results.Ok(new
        {
            datasetId = tableId.Value.ToString(),
            requiredFilterColumns = reqCols,
            requiredFilterColumnGroups = reqGroups
        });
    }

    internal static async Task<IResult> SetDatasetSyntheticSamplesHandler(
        string id,
        System.Collections.Generic.IReadOnlyList<System.Collections.Generic.Dictionary<string, object?>>? request,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Interfaces.ITableMetadataRepository metadataRepository,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Catalog.Profiling.ISyntheticSampleRowGenerator generator,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var tableId = await ResolveActiveDatasetAsync(id, metadataRepository, ct).ConfigureAwait(false);
        if (tableId is null)
        {
            return DatasetKeysNotFound();
        }

        var samples = request != null
            ? System.Linq.Enumerable.ToList(request.Select(r => (System.Collections.Generic.IReadOnlyDictionary<string, object?>)r))
            : (System.Collections.Generic.IReadOnlyList<System.Collections.Generic.IReadOnlyDictionary<string, object?>>)System.Array.Empty<System.Collections.Generic.IReadOnlyDictionary<string, object?>>();

        if (samples.Count > 100)
        {
            return Results.BadRequest(new { error = "Maximum allowed synthetic sample rows is 100." });
        }

        generator.RegisterSyntheticSamples(tableId.Value, samples);

        var existingMeta = await metadataRepository.GetTableMetadataAsync(tableId.Value, ct).ConfigureAwait(false);
        if (existingMeta != null)
        {
            await metadataRepository.UpsertTableMetadataAsync(existingMeta with
            {
                PredefinedSyntheticSamples = samples
            }, ct).ConfigureAwait(false);
        }

        return Results.Ok(new { dataset = id, registeredRows = samples.Count });
    }

    internal static async Task<IResult> GetDatasetSyntheticSamplesHandler(
        string id,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Interfaces.ITableMetadataRepository metadataRepository,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Catalog.Profiling.ISyntheticSampleRowGenerator generator,
        CancellationToken ct)
    {
        var tableId = await ResolveActiveDatasetAsync(id, metadataRepository, ct).ConfigureAwait(false);
        if (tableId is null)
        {
            return DatasetKeysNotFound();
        }

        var existingMeta = await metadataRepository.GetTableMetadataAsync(tableId.Value, ct).ConfigureAwait(false);
        var samples = existingMeta?.PredefinedSyntheticSamples is { Count: > 0 } p
            ? p
            : generator.GetRegisteredSyntheticSamples(tableId.Value) ?? (System.Collections.Generic.IReadOnlyList<System.Collections.Generic.IReadOnlyDictionary<string, object?>>)System.Array.Empty<System.Collections.Generic.IReadOnlyDictionary<string, object?>>();

        return Results.Ok(new { dataset = id, count = samples.Count, rows = samples });
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

    private static RequestContext CreateRequestContext(HttpContext httpContext, string? language = null)
    {
        var tenant = EndpointSecurity.GetRequestTenant(httpContext);
        var sid = httpContext.User.GetUserSid() ?? new Sid("anonymous");
        var roles = httpContext.User.GetUserRoles();
        return new RequestContext(tenant, sid, roles, httpContext.TraceIdentifier, httpContext.User, Language: language);
    }
}

public sealed record AdminSetDatasetStateDto(string State, string? Reason = null);

public sealed record DatasetIndexDto(
    string Name,
    System.Collections.Generic.IReadOnlyList<string> Columns,
    bool? IsUnique = null,
    bool? Unique = null);

public sealed record SetDatasetKeysDto(
    System.Collections.Generic.IReadOnlyList<string>? PrimaryKey = null,
    System.Collections.Generic.IReadOnlyList<string>? PrimaryKeyColumns = null,
    System.Collections.Generic.IReadOnlyList<DatasetIndexDto>? Indexes = null);


/// <summary>Issue #111 (C-4): replace the admin-source tags of a dataset.</summary>
public sealed record SetDatasetTagsDto(System.Collections.Generic.IReadOnlyList<string>? Tags);

/// <summary>Issue #111 (C-4): add and remove admin-source tags.</summary>
public sealed record ChangeDatasetTagsDto(System.Collections.Generic.IReadOnlyList<string>? Add, System.Collections.Generic.IReadOnlyList<string>? Remove);

/// <summary>Issue #111 (C-4): the requested tier name; parsed by the service, never bound to an enum.</summary>
public sealed record SetDatasetVisibilityDto(string? Visibility);

public sealed record DatasetTagDto(string Tag, string Source);

public sealed record DatasetCatalogSettingsDto(
    string DatasetId,
    System.Collections.Generic.IReadOnlyList<DatasetTagDto> Tags,
    string Visibility,
    bool TierEffective,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    System.Collections.Generic.IReadOnlyList<string>? RequiredFilterColumns = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    System.Collections.Generic.IReadOnlyList<System.Collections.Generic.IReadOnlyList<string>>? RequiredFilterColumnGroups = null);

public sealed record SetDatasetRequiredFiltersDto(
    System.Collections.Generic.IReadOnlyList<string>? RequiredFilterColumns = null,
    System.Collections.Generic.IReadOnlyList<System.Collections.Generic.IReadOnlyList<string>>? RequiredFilterColumnGroups = null);

public sealed record SetRelationCrossDataSourceDto(bool AllowCrossDataSource);
