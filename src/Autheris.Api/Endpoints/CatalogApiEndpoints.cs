namespace Autheris.Api.Endpoints;

using Autheris.Domain.Options.DataSources;
using Autheris.Application.Tenancy;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
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

public static partial class CatalogApiEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/catalog").WithIngressTransport(DataSourceTransportFlags.Rest);

        group.MapGet("/datasets", (
            ICatalogDiscoveryService discoveryService,
            HttpContext httpContext,
            CancellationToken ct) =>
            CatalogDiscoveryApiHelpers.ListDatasetsAsync(discoveryService, httpContext, ApplyCatalogLanguageAndVary, CreateRequestContext, ct))
        .RequireAuthorization()
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapGet("/datasets/{*datasetId}", (
            string datasetId,
            ICatalogDiscoveryService discoveryService,
            HttpContext httpContext,
            CancellationToken ct) =>
            CatalogDiscoveryApiHelpers.GetDatasetDetailAsync(datasetId, discoveryService, httpContext, ApplyCatalogLanguageAndVary, CreateRequestContext, ct))
        .RequireAuthorization()
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapGet("/domains", ListBusinessDomainsAsync)
        .RequireAuthorization()
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapGet("/containers", ListContainersAsync)
        .RequireAuthorization()
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapGet("/containers/{kind}/{id:guid}", GetContainerAsync)
        .RequireAuthorization()
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // Issue #802: whether the served GraphQL schema is behind the catalog. Authenticated: the answer reveals write timing.
        group.MapGet("/graphql-schema-status", (
            [Microsoft.AspNetCore.Mvc.FromServices] Autheris.GraphQL.Catalog.ICatalogSchemaFreshness? freshness,
            [Microsoft.AspNetCore.Mvc.FromServices] SchemaStatusAuditSampler? sampler,
            HttpContext httpContext,
            CancellationToken ct) => GetGraphQlSchemaStatusAsync(freshness, httpContext, sampler, ct))
        .RequireAuthorization()
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // Data sources disclose endpoint base URLs and authentication state: administrators only. A tenant-scoped GovernanceAdmin
        // is further confined to the tables of its tenant by the service. Any authenticated caller without a resolvable scope
        // (such as a tenantless GovernanceAdmin or TenantAdmin) is answered with a stable 403 instead of a 500 or empty list.
        group.MapGet("/datasources", async (
            ICatalogDiscoveryService discoveryService,
            IOptions<GatewayOptions> gatewayOptions,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var scope = CallerScopeResolver.Resolve(httpContext.User, TenantResolver.EffectiveDefaultOf(gatewayOptions.Value.Tenancy));
            if (scope == CallerScope.None && httpContext.User.Identity?.IsAuthenticated == true)
            {
                return CallerScopeResolver.HasInvalidTenantClaim(httpContext.User)
                    ? ApiProblems.Forbidden(ApiErrorCodes.TenantClaimInvalid, "The tenant claim of the caller is invalid.", httpContext)
                    : ApiProblems.Forbidden(
                        ApiErrorCodes.TenantUnresolved,
                        "The principal has no resolvable tenant: carry a tenant claim or configure Gateway:Tenancy:DefaultTenantId.",
                        httpContext);
            }

            RequestContext context;
            if (scope == CallerScope.Global)
            {
                TenantId globalTenant;
                try
                {
                    globalTenant = EndpointSecurity.GetRequestTenant(httpContext);
                }
                catch (TenantUnresolvedException)
                {
                    globalTenant = TenantId.Platform;
                }

                context = new RequestContext(
                    globalTenant,
                    httpContext.User.GetUserSid() ?? new Sid("anonymous"),
                    httpContext.User.GetUserRoles(),
                    httpContext.TraceIdentifier,
                    httpContext.User);
            }
            else
            {
                context = CreateRequestContext(httpContext);
            }

            var datasources = await discoveryService.ListDatasourcesAsync(context, ct);
            return Results.Ok(datasources);
        })
        .RequireAuthorization(GatewayPolicies.AdminOnly)
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapPost("/datasources", RegisterDatasourceHandler)
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        // Also expose on /api/governance/datasources (R-54)
        app.MapPost("/api/governance/datasources", RegisterDatasourceHandler)
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        group.MapGet("/search", (
            string? q,
            string? domain,
            int? limit,
            string? mode,
            ICatalogDiscoveryService discoveryService,
            HttpContext httpContext,
            CancellationToken ct) =>
            CatalogDiscoveryApiHelpers.SearchAsync(q, domain, limit, mode, discoveryService, httpContext, ApplyCatalogLanguageAndVary, CreateRequestContext, ct))
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
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The query parameter is required.", httpContext);
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
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The datasource identifier is invalid.", httpContext);
            }
            if (ResolveTenantRestriction(httpContext, out var restrictToTenant) is { } denied)
            {
                return denied;
            }

            try
            {
                var effectiveRequest = request ?? new DatasourceTestRequest();
                var result = await testingService.TestDatasourceAsync(id, effectiveRequest, httpContext.User, restrictToTenant, ct);
                return Results.Ok(result);
            }
            catch (System.Collections.Generic.KeyNotFoundException)
            {
                return ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "The datasource was not found.", httpContext);
            }
        })
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Delegated, AuditEventTypes.DatasourceTested);

        // B-15: PUT /api/v1/catalog/datasets/{id}/state
        group.MapPut("/datasets/{id}/state", SetDatasetStateHandler)
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApplied);

        // Issue #94: PUT /api/v1/catalog/datasets/{id}/keys (and /api/catalog/datasets/{id}/keys)
        group.MapPut("/datasets/{id}/keys", SetDatasetKeysHandler)
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.CatalogKeyDefinitionChanged);

        // PUT /api/v1/catalog/datasets/{id}/definition
        group.MapPut("/datasets/{id}/definition", PutDatasetDefinitionHandler)
        .WithRequestBodyLimit(1024 * 1024)
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

        // Discovery Artifacts (samples + column/table statistics)
        group.MapPut("/datasets/{id}/discovery-artifacts", PutDiscoveryArtifactsHandler)
        .WithRequestBodyLimit(Autheris.Application.Catalog.Services.CatalogDiscoverySettings.HardMaxArtifactBytes)
        .RequireAuthorization()
        .WithAudit(AuditLevel.Delegated, AuditEventTypes.CatalogDiscoveryArtifactsChanged);

        group.MapDelete("/datasets/{id}/discovery-artifacts", DeleteDiscoveryArtifactsHandler)
        .RequireAuthorization()
        .WithAudit(AuditLevel.Delegated, AuditEventTypes.CatalogDiscoveryArtifactsChanged);

        group.MapGet("/datasets/{id}/samples", GetDiscoverySamplesHandler)
        .RequireAuthorization()
        .WithAudit(AuditLevel.Delegated, AuditEventTypes.MetadataSampleRead);

        // Issue #111: dataset tags and discovery flags. RequireAuthorization() only: owners must reach the routes, so the
        // decision (admin class, owner or delegate) is taken inside DatasetCatalogAdminService.
        MapDatasetCatalogRoutes(group);
        var legacyCatalog = app.MapGroup("/api/catalog").WithIngressTransport(DataSourceTransportFlags.Rest);
        MapDatasetCatalogRoutes(legacyCatalog);

        group.MapPut("/relations/{relationId:guid}/cross-data-source", SetRelationCrossDataSourceAsync)
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.CrossSourceOptInChanged);

        return app;
    }

    internal static async Task<IResult> SetRelationCrossDataSourceAsync(
        Guid relationId,
        SetRelationCrossDataSourceDto request,
        HttpContext context,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Common.Interfaces.ITableRelationRepository relationRepo,
        CancellationToken ct)
    {
        if (!EndpointSecurity.IsCanonicalClusterAdmin(context.User))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var updated = await relationRepo.SetRelationCrossDataSourceOptInAsync(relationId, request.AllowCrossDataSource, ct);
        if (!updated)
        {
            return ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "The relation was not found.");
        }
        return Results.Ok(new { relationId, allowCrossDataSource = request.AllowCrossDataSource });
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

        group.MapPut("/datasets/{id}/discovery", PutDatasetDiscoveryHandler)
        .RequireAuthorization()
        .WithAudit(AuditLevel.Full, AuditEventTypes.CatalogVisibilityChanged);

        group.MapPut("/datasets/{id}/privacy", PutDatasetPrivacyHandler)
        .RequireAuthorization()
        .WithAudit(AuditLevel.Full, AuditEventTypes.CatalogMinGroupSizeChanged);
    }

    internal static async Task<IResult> PutDatasetPrivacyHandler(
        string id,
        SetDatasetPrivacyDto? request,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Governance.Interfaces.IDatasetCatalogAdminService service,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var edit = request is null || (request.MinGroupSize is null && !request.NonPersonal.HasValue)
            ? new Autheris.Application.Governance.Interfaces.DatasetCatalogEdit(null, null, null) { Malformed = true }
            : new Autheris.Application.Governance.Interfaces.DatasetCatalogEdit(null, null, null, request.MinGroupSize, request.NonPersonal);
        return MapCatalogResult(await service.ApplyAsync(httpContext.User, id, edit, "admin", ct).ConfigureAwait(false));
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
            ? new Autheris.Application.Governance.Interfaces.DatasetCatalogEdit(null, null, null) { Malformed = true }
            : new Autheris.Application.Governance.Interfaces.DatasetCatalogEdit(request.Tags, null, null);
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
            ? new Autheris.Application.Governance.Interfaces.DatasetCatalogEdit(null, null, null) { Malformed = true }
            : new Autheris.Application.Governance.Interfaces.DatasetCatalogEdit(null, request.Add, request.Remove);
        return MapCatalogResult(await service.ApplyAsync(httpContext.User, id, edit, "admin", ct).ConfigureAwait(false));
    }

    internal static async Task<IResult> PutDatasetDiscoveryHandler(
        string id,
        SetDatasetDiscoveryDto? request,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Governance.Interfaces.IDatasetCatalogAdminService service,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var edit = request is null || (!request.Hidden.HasValue && request.HiddenColumns is null && !request.SamplesDisabled.HasValue)
            ? new Autheris.Application.Governance.Interfaces.DatasetCatalogEdit(null, null, null) { Malformed = true }
            : new Autheris.Application.Governance.Interfaces.DatasetCatalogEdit(null, null, null, null, null, request.Hidden, request.HiddenColumns, request.SamplesDisabled);
        return MapCatalogResult(await service.ApplyAsync(httpContext.User, id, edit, "admin", ct).ConfigureAwait(false));
    }

    /// <summary>One mapping for all four routes; NotFound has one fixed body so unknown, inactive and non-manageable datasets cannot be told apart.</summary>
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
                    settings.MinGroupSize,
                    settings.NonPersonal,
                    result.Warning));
            case Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.Forbidden:
                return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "Access to the dataset is denied.");
            case Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.InvalidTag:
                return ApiProblems.BadRequest(ApiErrorCodes.InvalidTag, "The tag is invalid.");
            case Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.TagLimitExceeded:
                return ApiProblems.BadRequest(ApiErrorCodes.TagLimitExceeded, "The tag limit is exceeded.");
            case Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.InvalidColumns:
                return ApiProblems.BadRequest(ApiErrorCodes.InvalidColumns, "One or more column names are invalid.");
            case Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.InvalidRequest:
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The request is invalid.");
            case Autheris.Application.Governance.Interfaces.DatasetCatalogOutcome.Unavailable:
                return ApiProblems.ServiceUnavailable();
            default:
                return ApiProblems.Indistinguishable(StatusCodes.Status404NotFound, ApiErrorCodes.ResourceNotFound, "The dataset was not found.");
        }
    }

    private static IResult DatasetKeysNotFound() => ApiProblems.Indistinguishable(StatusCodes.Status404NotFound, ApiErrorCodes.ResourceNotFound, "The dataset was not found.");

    /// <summary>Resolves an active dataset by table id or domain.schema.table (shared with the row filter strategy endpoints).</summary>
    private static Task<TableIdentifier?> ResolveActiveDatasetAsync(
        string? id,
        Autheris.Application.Common.Interfaces.ITableMetadataRepository metadataRepository,
        CancellationToken ct)
        => Autheris.Application.Governance.Services.DatasetIdResolver.ResolveActiveAsync(id, metadataRepository, ct);

    /// <summary>Resolves the tenant a governance admin is confined to (null for global scope); returns a denial result when the tenant cannot be resolved.</summary>
    private static IResult? ResolveTenantRestriction(HttpContext httpContext, out TenantId? restrictToTenant)
    {
        restrictToTenant = null;
        var gatewayOptions = (httpContext.RequestServices?.GetService(typeof(IOptions<GatewayOptions>)) as IOptions<GatewayOptions>)?.Value ?? new GatewayOptions();
        var defaultTenant = TenantResolver.EffectiveDefaultOf(gatewayOptions.Tenancy);
        var scope = CallerScopeResolver.Resolve(httpContext.User, defaultTenant);

        if (scope == CallerScope.None && httpContext.User.Identity?.IsAuthenticated == true &&
            CallerScopeResolver.HoldsGovernanceAdminRole(httpContext.User))
        {
            return CallerScopeResolver.HasInvalidTenantClaim(httpContext.User)
                ? ApiProblems.Forbidden(ApiErrorCodes.TenantClaimInvalid, "The tenant claim of the caller is invalid.", httpContext)
                : ApiProblems.Forbidden(
                    ApiErrorCodes.TenantUnresolved,
                    "The principal has no resolvable tenant: carry a tenant claim or configure Gateway:Tenancy:DefaultTenantId.",
                    httpContext);
        }

        restrictToTenant = scope == CallerScope.Tenant
            ? CallerScopeResolver.ResolutionOf(httpContext.User, defaultTenant).Tenant
            : null;

        return null;
    }

    internal static async Task<IResult> SetDatasetKeysHandler(
        string id,
        SetDatasetKeysDto? request,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Common.Interfaces.ITableMetadataRepository metadataRepository,
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
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The key definition payload is required.", httpContext);
        }

        var tableId = await ResolveActiveDatasetAsync(id, metadataRepository, ct).ConfigureAwait(false);
        if (tableId is null)
        {
            return DatasetKeysNotFound();
        }

        var pkColumns = request.PrimaryKey ?? request.PrimaryKeyColumns ?? Array.Empty<string>();

        // Issue #390 (I390-SC-06/13): directions are the closed strings ASC/DESC with exactly one entry per column. Any other value or a count mismatch
        // is refused before anything is persisted, with the generic message (the value is never echoed).
        IReadOnlyList<KeyColumnDirection>? pkDirections = null;
        if (request.PrimaryKeyDirections is not null && !TryParseDirections(request.PrimaryKeyDirections, pkColumns.Count, out pkDirections))
        {
            return InvalidKeyDefinition(httpContext);
        }

        if (request.Indexes != null && System.Linq.Enumerable.Any(request.Indexes, i => i is { Unique: not null, IsUnique: not null } && i.Unique != i.IsUnique))
        {
            // `unique` is canonical, `isUnique` an accepted alias; contradicting values are refused with the generic message.
            return InvalidKeyDefinition(httpContext);
        }

        var indexList = new System.Collections.Generic.List<TableIndexDefinition>();
        if (request.Indexes != null)
        {
            foreach (var i in request.Indexes)
            {
                IReadOnlyList<KeyColumnDirection>? indexDirections = null;
                if (i?.Directions is not null && !TryParseDirections(i.Directions, i.Columns?.Count ?? 0, out indexDirections))
                {
                    return InvalidKeyDefinition(httpContext);
                }

                indexList.Add(new TableIndexDefinition(
                    i?.Name ?? string.Empty,
                    i?.Columns ?? Array.Empty<string>(),
                    i?.Unique ?? i?.IsUnique ?? false,
                    indexDirections));
            }
        }

        System.Collections.Generic.IReadOnlyList<TableIndexDefinition> indexes = indexList;

        // The caps are enforced by the validator inside the repository before anything is allocated per element.
        TableKeyDefinitionChange? change;
        try
        {
            change = await metadataRepository.ReplaceTableKeyDefinitionAsync(tableId.Value, pkColumns, indexes, pkDirections, ct).ConfigureAwait(false);
        }
        catch (TableDefinitionException)
        {
            return InvalidKeyDefinition(httpContext);
        }

        if (change is null)
        {
            return DatasetKeysNotFound();
        }

        // a changed definition is traceable in the HMAC-chained audit log (identifiers only, no data). Post-commit: a client
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
                    : KeyDefinitionAuditView(change.Before),
                after = KeyDefinitionAuditView(change.After)
            })
        }, CancellationToken.None).ConfigureAwait(false);

        var response = KeyDefinitionAuditView(change.After);
        response["datasetId"] = tableId.Value.ToString();
        return Results.Ok(response);
    }

    /// <summary>Parses <c>ASC</c>/<c>DESC</c> strings (ignoring case); false for any other value, a count other than <paramref name="expectedCount"/> or more than the key column limit.</summary>
    private static bool TryParseDirections(
        System.Collections.Generic.IReadOnlyList<string?> values,
        int expectedCount,
        out IReadOnlyList<KeyColumnDirection>? directions)
    {
        directions = null;
        if (values.Count != expectedCount || values.Count > TableKeyDefinitionValidator.MaxColumnsPerKey)
        {
            return false;
        }

        var parsed = new KeyColumnDirection[values.Count];
        for (var n = 0; n < parsed.Length; n++)
        {
            if (!KeyDirections.TryParse(values[n], out parsed[n]))
            {
                return false;
            }
        }

        directions = parsed;
        return true;
    }

    private static IResult InvalidKeyDefinition(HttpContext httpContext) =>
        ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The table key or index definition is invalid.", httpContext);

    /// <summary>
    /// Audit view of a key definition (identifiers only). Issue #390: <c>primaryKeyDirections</c> appears only when a column is descending, as the
    /// closed strings <c>ASC</c>/<c>DESC</c>, so the details of a table without a declared direction are unchanged; index directions serialize with the index.
    /// </summary>
    private static System.Collections.Generic.Dictionary<string, object> KeyDefinitionAuditView(TableKeyDefinition definition)
    {
        var view = new System.Collections.Generic.Dictionary<string, object>
        {
            ["primaryKey"] = definition.PrimaryKey,
            ["indexes"] = definition.Indexes
        };
        if (KeyDirections.AnyDescending(definition.PrimaryKeyDirections))
        {
            view["primaryKeyDirections"] = KeyDirections.ToWire(definition.PrimaryKeyDirections);
        }

        return view;
    }

    internal static async Task<IResult> SetDatasetRequiredFiltersHandler(
        string id,
        SetDatasetRequiredFiltersDto? request,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Common.Interfaces.ITableMetadataRepository metadataRepository,
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
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "A referenced column does not exist on the table.", httpContext);
            }
        }
        foreach (var group in reqGroups)
        {
            foreach (var c in group)
            {
                if (!colNames.Contains(c))
                {
                    return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "A referenced column does not exist on the table.", httpContext);
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

    internal static async Task<IResult> PutDiscoveryArtifactsHandler(
        string id,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Catalog.Discovery.IDiscoveryArtifactService artifactService,
        [Microsoft.AspNetCore.Mvc.FromServices] Microsoft.Extensions.Options.IOptions<Autheris.Application.Catalog.Services.CatalogDiscoveryOptions>? discoveryOptions,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var configuredMax = discoveryOptions?.Value.Discovery.MaxArtifactBytes ?? Autheris.Application.Catalog.Services.CatalogDiscoverySettings.HardMaxArtifactBytes;
        var hardCap = Math.Clamp(configuredMax, 1L, Autheris.Application.Catalog.Services.CatalogDiscoverySettings.HardMaxArtifactBytes);
        var (rawBytes, limitExceeded) = await ReadBoundedBodyAsync(httpContext.Request.Body, hardCap, ct).ConfigureAwait(false);
        if (limitExceeded)
        {
            return ApiProblems.BadRequest(
                ApiErrorCodes.InvalidArtifact,
                $"Artifact payload exceeds the configured limit of {hardCap} bytes.",
                httpContext,
                new Dictionary<string, object?> { ["violation"] = "BODY_TOO_LARGE" });
        }

        var result = await artifactService.UpsertAsync(httpContext.User, id, rawBytes, source: "rest", ct).ConfigureAwait(false);
        return MapArtifactWriteResult(result, httpContext);
    }

    internal static async Task<IResult> DeleteDiscoveryArtifactsHandler(
        string id,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Catalog.Discovery.IDiscoveryArtifactService artifactService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var result = await artifactService.DeleteAsync(httpContext.User, id, source: "rest", ct).ConfigureAwait(false);
        return MapArtifactWriteResult(result, httpContext);
    }

    internal static async Task<IResult> GetDiscoverySamplesHandler(
        string id,
        [Microsoft.AspNetCore.Mvc.FromQuery] int? limit,
        [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Catalog.Discovery.IDiscoveryArtifactService artifactService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var result = await artifactService.GetSamplesAsync(
            httpContext.User,
            id,
            limit,
            Autheris.Application.Catalog.Discovery.DiscoverySurface.Rest,
            ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            Autheris.Application.Catalog.Discovery.DiscoveryArtifactOutcome.Unauthenticated =>
                ApiProblems.Unauthorized(result.ErrorCode ?? ApiErrorCodes.Unauthorized, result.Message ?? "Authentication is required.", httpContext),
            Autheris.Application.Catalog.Discovery.DiscoveryArtifactOutcome.Forbidden =>
                ApiProblems.Forbidden(result.ErrorCode ?? ApiErrorCodes.DiscoveryNotPermitted, result.Message ?? "Catalog discovery is not permitted.", httpContext),
            Autheris.Application.Catalog.Discovery.DiscoveryArtifactOutcome.Unavailable =>
                ApiProblems.ServiceUnavailable(result.ErrorCode ?? ApiErrorCodes.ServiceUnavailable, result.Message ?? "Catalog discovery is temporarily unavailable.", httpContext),
            _ => Results.Ok(result.Payload ?? new Autheris.Application.Catalog.Discovery.DiscoverySamplesResponse(false, []))
        };
    }

    private static IResult MapArtifactWriteResult(
        Autheris.Application.Catalog.Discovery.DiscoveryArtifactWriteResult result,
        HttpContext httpContext) => result.Outcome switch
    {
        Autheris.Application.Catalog.Discovery.DiscoveryArtifactOutcome.Ok => Results.Ok(new
        {
            dataset = result.DatasetId,
            contentSha256 = result.ContentSha256,
            sampleRowCount = result.SampleRowCount,
            statisticsColumnCount = result.StatisticsColumnCount
        }),
        Autheris.Application.Catalog.Discovery.DiscoveryArtifactOutcome.NoContent => Results.NoContent(),
        Autheris.Application.Catalog.Discovery.DiscoveryArtifactOutcome.Unauthenticated =>
            ApiProblems.Unauthorized(ApiErrorCodes.Unauthorized, result.Message ?? "Authentication is required.", httpContext),
        Autheris.Application.Catalog.Discovery.DiscoveryArtifactOutcome.NotFound => DatasetKeysNotFound(),
        Autheris.Application.Catalog.Discovery.DiscoveryArtifactOutcome.Forbidden =>
            ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, result.Message ?? "Access denied.", httpContext),
        Autheris.Application.Catalog.Discovery.DiscoveryArtifactOutcome.InvalidArtifact =>
            ApiProblems.BadRequest(
                ApiErrorCodes.InvalidArtifact,
                result.Message ?? "Artifact payload is invalid.",
                httpContext,
                string.IsNullOrEmpty(result.ViolationCode) ? null : new Dictionary<string, object?> { ["violation"] = result.ViolationCode }),
        _ => ApiProblems.ServiceUnavailable(ApiErrorCodes.ServiceUnavailable, result.Message ?? "The service is temporarily unavailable.", httpContext)
    };

    private static async Task<(byte[] Bytes, bool LimitExceeded)> ReadBoundedBodyAsync(
        System.IO.Stream body,
        long maxBytes,
        CancellationToken ct)
    {
        using var ms = new System.IO.MemoryStream();
        var buffer = new byte[Math.Min(16384, (int)Math.Min(maxBytes + 1, int.MaxValue))];
        long total = 0;
        while (true)
        {
            var read = await body.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return (ms.ToArray(), false);
            }

            total += read;
            if (total > maxBytes)
            {
                return ([], true);
            }

            ms.Write(buffer, 0, read);
        }
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
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The state is required.", httpContext);
        }

        if (ResolveTenantRestriction(httpContext, out var restrictToTenant) is { } denied)
        {
            return denied;
        }

        var adminSid = httpContext.User.GetUserSid()?.Value ?? httpContext.User.Identity?.Name ?? "system-admin";
        var planningRequest = new AdminSetDatasetStateRequest(id, request.State, request.Reason);
        var result = await accessPlanningService.SetDatasetStateAsync(planningRequest, adminSid, EndpointSecurity.GetRequestTenant(httpContext).Value, restrictToTenant, ct);
        if (!result.Success)
        {
            return ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "The dataset was not found.", httpContext);
        }

        return Results.Ok(result);
    }

    internal static async Task<IResult> RegisterDatasourceHandler(
        DatasourceRegistrationRequest request,
        ICatalogDiscoveryService discoveryService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Domain))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The name and the domain are required.", httpContext);
        }

        var context = CreateRequestContext(httpContext);

        // GHSA-v5c2: the domain must be the caller's own tenant unless the caller is a canonical ClusterAdmin.
        if (!EndpointSecurity.IsCanonicalClusterAdmin(httpContext.User) &&
            !string.Equals(request.Domain.Trim(), context.TenantId.Value, StringComparison.OrdinalIgnoreCase))
        {
            return ApiProblems.Forbidden(httpContext: httpContext);
        }

        try
        {
            var result = await discoveryService.RegisterDatasourceAsync(request, context, ct);
            return Results.Ok(result);
        }
        catch (ArgumentException ex) when (ex.ParamName == "secretRef")
        {
            ApiProblems.LogFailure(httpContext, ex, ApiErrorCodes.InvalidSecretRef);
            return ApiProblems.BadRequest(ApiErrorCodes.InvalidSecretRef, "The secret reference is invalid.", httpContext);
        }
    }

    internal static string ApplyCatalogLanguageAndVary(HttpContext httpContext)
    {
        var lang = httpContext.Request.Query.TryGetValue("lang", out var langVal) ? langVal.ToString() : null;
        var acceptLanguage = httpContext.Request.Headers.AcceptLanguage.ToString();
        var requestedLang = !string.IsNullOrWhiteSpace(lang) ? lang : (!string.IsNullOrWhiteSpace(acceptLanguage) ? acceptLanguage : null);
        var languageNegotiator = httpContext.RequestServices?.GetService(typeof(ILanguageNegotiator)) as ILanguageNegotiator ?? new LanguageNegotiator();
        var localizationOptions = (httpContext.RequestServices?.GetService(typeof(IOptions<CatalogLocalizationOptions>)) as IOptions<CatalogLocalizationOptions>)?.Value ?? new CatalogLocalizationOptions();
        var targetLang = languageNegotiator.NegotiateLanguage(requestedLang, localizationOptions.SupportedLanguages, localizationOptions.DefaultLanguage);
        httpContext.Response.Headers.Append("Content-Language", targetLang);
        httpContext.Response.Headers.Append("Vary", "Accept-Language");
        if (httpContext.Request.Headers.ContainsKey("X-Gateway-Contract") ||
            httpContext.Items.ContainsKey(Autheris.Application.Governance.Contracts.SchemaContractConstants.ContractItemKey))
        {
            httpContext.Response.Headers.Append("Vary", "X-Gateway-Contract");
        }
        return targetLang;
    }

    private static RequestContext CreateRequestContext(HttpContext httpContext, string? language = null)
    {
        var tenant = EndpointSecurity.GetRequestTenant(httpContext);
        var sid = httpContext.User.GetUserSid() ?? new Sid("anonymous");
        var roles = httpContext.User.GetUserRoles();
        return new RequestContext(tenant, sid, roles, httpContext.TraceIdentifier, httpContext.User, Language: language);
    }
}
