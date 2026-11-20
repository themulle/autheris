namespace Autheris.Modules.Dbt;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Security;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Modules.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class DbtEndpoints
{
    private static readonly GatewayRoleEvaluator RoleEvaluator = new GatewayRoleEvaluator();

    public static void MapEndpoints(IModuleEndpointRegistrar endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var app = endpoints.MapGroup("/modules/dbt");

        // dbt Ingestion & Exposure Endpoints (F-DATA-11)
        app.MapPost("/sync", HandleSyncAsync)
          .WithModuleRequestBodyLimit(100 * 1024 * 1024) // SEC M-01: explicit large-body exception to the global Kestrel limit
          .WithModuleAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        // dbt Governance Ingestion Endpoint (R-24)
        app.MapPost("/governance", async (
            [FromServices] IModuleRequestContext requestContext,
            HttpContext context,
            [FromServices] IDbtMetadataIngestionService dbtService) =>
        {
            // The direct governance import writes masking rules, sensitivities, virtual filters and access profiles; the DbtAdmin
            // role alone (manifest / run-results automation) does not authorize it. Canonical ClusterAdmin only (OD-9).
            if (!requestContext.IsGlobalGovernanceAdmin(context.User))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // Virtual filters and access profiles are saved as the calling user (never as the sync, so the approval workflow
            // applies) in the server-resolved request tenant; only a canonical ClusterAdmin may import into another tenant.
            Autheris.Domain.Security.SecurityPrincipalContext security;
            try
            {
                security = requestContext.GetSecurityContext(context);
            }
            catch (UnauthorizedAccessException)
            {
                // Authenticated without a resolvable SID: refused (403) instead of surfacing as a 500.
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var caller = new DbtGovernanceImportCaller(
                new VirtualFilterActor(security.UserSid, IsSync: false, context.User?.GetUserIdentifiers()),
                requestContext.GetRequestTenant(context),
                requestContext.IsCanonicalClusterAdmin(context.User),
                IsGovernanceAdmin: requestContext.IsGlobalGovernanceAdmin(context.User));

            const string governanceTooLarge = "Governance file size exceeds maximum allowed size (100 MB).";
            var limitError = requestContext.TryApplyBodyLimit(context.Request, 100 * 1024 * 1024, governanceTooLarge);
            if (limitError != null)
            {
                return limitError;
            }

            var dryRun = context.Request.Query.ContainsKey("dryRun") &&
                         bool.TryParse(context.Request.Query["dryRun"], out var dr) && dr;
            var replace = (context.Request.Query.ContainsKey("replace") &&
                           bool.TryParse(context.Request.Query["replace"], out var rep) && rep) ||
                          string.Equals(context.Request.Query["mode"], "replace", StringComparison.OrdinalIgnoreCase);

            return await requestContext.WithBodyLimitAsync(async () =>
            {
                var result = await dbtService.IngestGovernanceStreamAsync(context.Request.Body, caller, dryRun, replace, context.RequestAborted);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            }, governanceTooLarge);
        })
          .WithModuleRequestBodyLimit(100 * 1024 * 1024)
          .WithModuleAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        app.MapGet("/exposures", async (
            [FromServices] IModuleRequestContext requestContext,
            [FromServices] IDbtExposurePublisher exposurePublisher,
            HttpContext context) =>
        {
            // Review E-7: dbt state is global and carries no table ownership; a plain DataOwner role may not read it.
            var isPrivileged = requestContext.IsGlobalGovernanceAdmin(context.User);
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var yaml = await exposurePublisher.GenerateExposuresYamlAsync(context.RequestAborted);
            return Results.Content(yaml, "text/yaml; charset=utf-8");
        }).WithModuleAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapGet("/proposals", async (
            [FromServices] IModuleRequestContext requestContext,
            [FromServices] IDbtMetadataIngestionService dbtService,
            HttpContext context) =>
        {
            // Review E-7: dbt state is global and carries no table ownership; a plain DataOwner role may not read it.
            var isPrivileged = requestContext.IsGlobalGovernanceAdmin(context.User);
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            TableIdentifier? table = null;
            if (context.Request.Query.TryGetValue("database", out var db) &&
                context.Request.Query.TryGetValue("schema", out var schema) &&
                context.Request.Query.TryGetValue("table", out var tableName))
            {
                table = new TableIdentifier(db!, schema!, tableName!);
            }

            var proposals = await dbtService.GetPendingProposalsAsync(table, context.RequestAborted);
            return Results.Ok(proposals);
        }).WithModuleAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapPost("/proposals/{id:guid}/approve", async (
            [FromServices] IModuleRequestContext requestContext,
            Guid id,
            [FromServices] IDbtMetadataIngestionService dbtService,
            HttpContext context) =>
        {
            // SEC M-11: dbt proposals carry no tenant and change global masking metadata ->
            // canonical ClusterAdmin only (OD-9) may decide.
            if (!requestContext.IsGlobalGovernanceAdmin(context.User))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // EP-13: the decision is attributed to the canonical caller identity (SID), never to a display name or a placeholder.
            var user = requestContext.GetCallerIdentity(context);
            if (string.IsNullOrWhiteSpace(user))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            try
            {
                var approved = await dbtService.ApproveProposalAsync(id, user, context.RequestAborted);
                return Results.Ok(approved);
            }
            catch (KeyNotFoundException)
            {
                return requestContext.NotFound("The proposal was not found.", context);
            }
            catch (InvalidOperationException ex)
            {
                // R-EXT-1: not pending, not applicable (RLS) or blocked by the ratchet; the status is unchanged.
                LogFailure(context, ex, "DBT_PROPOSAL_CONFLICT");
                return requestContext.Problem(StatusCodes.Status409Conflict, "DBT_PROPOSAL_CONFLICT", "The proposal cannot be approved in its current state.", context);
            }
        }).WithModuleAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);

        app.MapPost("/proposals/{id:guid}/reject", async (
            [FromServices] IModuleRequestContext requestContext,
            Guid id,
            [FromServices] IDbtMetadataIngestionService dbtService,
            HttpContext context) =>
        {
            // SEC M-11: dbt proposals carry no tenant and change global masking metadata ->
            // canonical ClusterAdmin only (OD-9) may decide.
            if (!requestContext.IsGlobalGovernanceAdmin(context.User))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // EP-13: the decision is attributed to the canonical caller identity (SID), never to a display name or a placeholder.
            var user = requestContext.GetCallerIdentity(context);
            if (string.IsNullOrWhiteSpace(user))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            try
            {
                var rejected = await dbtService.RejectProposalAsync(id, user, context.RequestAborted);
                return Results.Ok(rejected);
            }
            catch (KeyNotFoundException)
            {
                return requestContext.NotFound("The proposal was not found.", context);
            }
            catch (InvalidOperationException ex)
            {
                LogFailure(context, ex, "DBT_PROPOSAL_CONFLICT");
                return requestContext.Problem(StatusCodes.Status409Conflict, "DBT_PROPOSAL_CONFLICT", "The proposal cannot be rejected in its current state.", context);
            }
        }).WithModuleAudit(AuditLevel.Full, AuditEventTypes.ConsentDenied);

        app.MapPost("/validate-contract", async (
            [FromServices] IModuleRequestContext requestContext,
            HttpContext context,
            [FromServices] IDbtContractValidator validator) =>
        {
            // Review E-7 / SG-13: dbt state is global and carries no table ownership; only canonical ClusterAdmin (OD-9) or DbtAdmin.
            var isPrivileged = requestContext.IsGlobalGovernanceAdmin(context.User) ||
                               context.User.IsInRole("DbtAdmin");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // SEC M-07: Server-side body limit instead of a bypassable Content-Length check.
            const string manifestTooLarge = "Manifest size exceeds maximum allowed size (100 MB).";
            var limitError = requestContext.TryApplyBodyLimit(context.Request, 100 * 1024 * 1024, manifestTooLarge);
            if (limitError != null)
            {
                return limitError;
            }

            return await requestContext.WithBodyLimitAsync(async () =>
            {
                var result = await validator.ValidateContractsStreamAsync(context.Request.Body, context.RequestAborted);
                return result.IsCompatible ? Results.Ok(result) : Results.UnprocessableEntity(result);
            }, manifestTooLarge);
        })
          .WithModuleRequestBodyLimit(100 * 1024 * 1024) // SEC M-01: explicit large-body exception to the global Kestrel limit
          .WithModuleAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // dbt Health & Circuit Breaker Endpoints (F-DBT-1)
        app.MapPost("/run-results", async (
            [FromServices] IModuleRequestContext requestContext,
            HttpContext context,
            [FromServices] IDbtHealthCircuitBreaker circuitBreaker) =>
        {
            // Review E-7: this endpoint changes global state (metadata, quarantine); a DataOwner role is not enough because
            // it carries no table ownership here. Only global governance administrators or the dedicated DbtAdmin role.
            var isPrivileged = requestContext.IsGlobalGovernanceAdmin(context.User) ||
                               context.User.IsInRole("DbtAdmin");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // SEC M-07: Server-side body limit instead of a bypassable Content-Length check.
            const string runResultsTooLarge = "Run results payload exceeds maximum allowed size (50 MB).";
            var limitError = requestContext.TryApplyBodyLimit(context.Request, 50 * 1024 * 1024, runResultsTooLarge);
            if (limitError != null)
            {
                return limitError;
            }

            return await requestContext.WithBodyLimitAsync(async () =>
            {
                var tenant = requestContext.GetRequestTenant(context);
                var report = await circuitBreaker.RecordRunResultsAsync(context.Request.Body, tenant, context.RequestAborted);
                return Results.Ok(report);
            }, runResultsTooLarge);
        })
          .WithModuleRequestBodyLimit(50 * 1024 * 1024) // SEC M-01: explicit large-body exception to the global Kestrel limit
          .WithModuleAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        app.MapGet("/health", async (
            [FromServices] IModuleRequestContext requestContext,
            HttpContext context,
            [FromServices] IDbtHealthCircuitBreaker circuitBreaker,
            [FromServices] IDbtTestTargetMap? testTargetMap) =>
        {
            // Review E-7 / SG-13: dbt state is global and carries no table ownership; only canonical ClusterAdmin (OD-9) or DbtAdmin.
            var isPrivileged = requestContext.IsGlobalGovernanceAdmin(context.User) ||
                               context.User.IsInRole("DbtAdmin");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (context.Request.Query.TryGetValue("table", out var tableName) && !string.IsNullOrWhiteSpace(tableName))
            {
                var reqTenant = requestContext.GetRequestTenant(context);
                TableIdentifier tableId;
                if (testTargetMap != null && testTargetMap.TryResolveModel(reqTenant, tableName!, out var resolvedId))
                {
                    tableId = resolvedId;
                }
                else
                {
                    var db = context.Request.Query.TryGetValue("database", out var dbVal) && !string.IsNullOrWhiteSpace(dbVal) ? dbVal.ToString() : reqTenant.Value;
                    var schema = context.Request.Query.TryGetValue("schema", out var schemaVal) && !string.IsNullOrWhiteSpace(schemaVal) ? schemaVal.ToString() : "default";
                    tableId = new TableIdentifier(db, schema, tableName!);
                }
                var health = await circuitBreaker.GetTableHealthAsync(tableId, context.RequestAborted);
                return Results.Ok(health);
            }

            var allStates = await circuitBreaker.GetAllHealthStatesAsync(context.RequestAborted);
            return Results.Ok(allStates);
        }).WithModuleAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapPost("/health/reset", async (
            [FromServices] IModuleRequestContext requestContext,
            HttpContext context,
            [FromServices] IDbtHealthCircuitBreaker circuitBreaker,
            [FromServices] IDbtTestTargetMap? testTargetMap) =>
        {
            // Review E-7: this endpoint changes global state (metadata, quarantine); a DataOwner role is not enough because
            // it carries no table ownership here. Only global governance administrators or the dedicated DbtAdmin role.
            var isPrivileged = requestContext.IsGlobalGovernanceAdmin(context.User) ||
                               context.User.IsInRole("DbtAdmin");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (context.Request.Query.TryGetValue("table", out var tableName) && !string.IsNullOrWhiteSpace(tableName))
            {
                var reqTenant = requestContext.GetRequestTenant(context);
                TableIdentifier tableId;
                if (testTargetMap != null && testTargetMap.TryResolveModel(reqTenant, tableName!, out var resolvedId))
                {
                    tableId = resolvedId;
                }
                else
                {
                    var db = context.Request.Query.TryGetValue("database", out var dbVal) && !string.IsNullOrWhiteSpace(dbVal) ? dbVal.ToString() : reqTenant.Value;
                    var schema = context.Request.Query.TryGetValue("schema", out var schemaVal) && !string.IsNullOrWhiteSpace(schemaVal) ? schemaVal.ToString() : "default";
                    tableId = new TableIdentifier(db, schema, tableName!);
                }
                await circuitBreaker.ResetTableHealthAsync(tableId, context.RequestAborted);
                return Results.Ok(new { message = $"Table '{tableId}' health reset to healthy." });
            }

            // SEC M-11: Resetting all health states is a global action -> canonical ClusterAdmin only (OD-9).
            if (!requestContext.IsGlobalGovernanceAdmin(context.User))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            await circuitBreaker.ResetAllAsync(context.RequestAborted);
            return Results.Ok(new { message = "All table health states reset to healthy." });
        }).WithModuleAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        // Wave 4 / Issue #38: dbt Semantic Manifest Ingestion & Metrics Catalog
        app.MapPost("/semantic-manifest", HandleImportSemanticManifestAsync)
          .WithModuleRequestBodyLimit(10 * 1024 * 1024)
          .WithModuleAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        app.MapGet("/metrics", HandleListDbtMetricsAsync)
          .WithModuleAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

    }

    private const int MaxAuditedCreatedTables = 100;

    /// <summary>Logs the original exception server-side (never returned to the client).</summary>
    private static void LogFailure(HttpContext context, Exception exception, string code)
    {
        context.RequestServices.GetService<ILoggerFactory>()?
            .CreateLogger(typeof(DbtEndpoints).FullName!)
            .LogWarning(exception, "Request failed with code {ErrorCode} (trace {TraceId}).", code, context.TraceIdentifier);
    }

    /// <summary>Issue #282: the created table names (capped) and counts go into the audit entry; never columns or secrets.</summary>
    internal static void WriteSyncAudit(HttpContext context, DbtSyncResult? result, bool dryRun)
    {
        if (context.Features.Get<AuditContext>() is not { } auditContext)
        {
            return;
        }

        var created = result?.CreatedTables ?? [];
        auditContext.AdditionalDetails["dry_run"] = dryRun;
        auditContext.AdditionalDetails["created_tables_count"] = created.Count;
        auditContext.AdditionalDetails["created_tables"] = created.Count > MaxAuditedCreatedTables
            ? created.Take(MaxAuditedCreatedTables).ToArray()
            : created.ToArray();
        auditContext.AdditionalDetails["created_tables_truncated"] = created.Count > MaxAuditedCreatedTables;
        auditContext.AdditionalDetails["skipped_items_count"] = result?.SkippedItems.Count ?? 0;
    }

    internal static async Task<IResult> HandleSyncAsync(
        HttpContext context,
        IModuleRequestContext requestContext,
        [FromServices] IDbtMetadataIngestionService dbtService)
    {
        // Review E-7: this endpoint changes global state (metadata, quarantine); a DataOwner role is not enough because
        // it carries no table ownership here. Only global governance administrators or the dedicated DbtAdmin role.
        var isPrivileged = RoleEvaluator.HasRole(context.User, GatewayRole.GovernanceAdmin) ||
                           requestContext.IsCanonicalClusterAdmin(context.User) ||
                           context.User.IsInRole("DbtAdmin");
        if (!isPrivileged)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // SEC M-07: Server-side body limit instead of a bypassable Content-Length check.
        const string manifestTooLarge = "Manifest size exceeds maximum allowed size (100 MB).";
        var limitError = requestContext.TryApplyBodyLimit(context.Request, 100 * 1024 * 1024, manifestTooLarge);
        if (limitError != null)
        {
            return limitError;
        }

        var dryRun = context.Request.Query.ContainsKey("dryRun") &&
                     bool.TryParse(context.Request.Query["dryRun"], out var dr) && dr;

        Autheris.Domain.Security.SecurityPrincipalContext security;
        try
        {
            security = requestContext.GetSecurityContext(context);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var caller = new DbtGovernanceImportCaller(
            new VirtualFilterActor(security.UserSid, IsSync: false, context.User?.GetUserIdentifiers()),
            requestContext.GetRequestTenant(context),
            requestContext.IsCanonicalClusterAdmin(context.User),
            requestContext.IsGlobalGovernanceAdmin(context.User),
            security.HasRole("DataOwner") || (context.User?.IsInRole("DataOwner") == true));

        DbtSyncResult? syncResult = null;
        try
        {
            return await requestContext.WithBodyLimitAsync(async () =>
            {
                syncResult = await dbtService.IngestManifestStreamAsync(context.Request.Body, caller, dryRun, context.RequestAborted);
                return syncResult.Success ? Results.Ok(syncResult) : Results.BadRequest(syncResult);
            }, manifestTooLarge);
        }
        finally
        {
            WriteSyncAudit(context, syncResult, dryRun);
        }
    }

    internal static async Task<IResult> HandleImportSemanticManifestAsync(
        HttpContext context,
        IModuleRequestContext requestContext,
        [FromServices] IDbtSemanticManifestIngestionService ingestionService,
        [FromServices] SemanticTenancy semanticTenancy)
    {
        var isPrivileged = RoleEvaluator.HasRole(context.User, GatewayRole.GovernanceAdmin) ||
                           requestContext.IsCanonicalClusterAdmin(context.User) ||
                           context.User.IsInRole("DbtAdmin");
        if (!isPrivileged)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // M-1 / A1-3: the import tenant is the server-resolved request tenant only (never a body or query value). A caller without
        // catalog standing (no SID in either mode, no tenant claim in Multi) cannot import; the catalog additionally rejects the
        // legacy tenant id in Multi.
        if (semanticTenancy.ResolveCallerTenant(context.User) is null)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var (payload, tooLarge) = await requestContext.TryReadBodyAsync(
            context.Request,
            10 * 1024 * 1024,
            "Semantic manifest payload exceeds maximum allowed size (10 MB).",
            context.RequestAborted);
        if (payload == null)
        {
            return tooLarge!;
        }

        try
        {
            var manifest = await ingestionService.ParseAndIngestAsync(
                payload,
                requestContext.GetRequestTenant(context),
                requestContext.GetCallerIdentity(context),
                context.RequestAborted);

            // M-5: version, content hash and counts go into the audit entry; never the manifest text.
            if (context.Features.Get<Autheris.Domain.Audit.AuditContext>() is { } auditContext)
            {
                auditContext.AdditionalDetails["manifest_version"] = manifest.Version;
                auditContext.AdditionalDetails["content_hash"] = SemanticManifestJson.ComputeHash(SemanticManifestJson.Serialize(manifest));
                auditContext.AdditionalDetails["models_count"] = manifest.SemanticModels.Count;
                auditContext.AdditionalDetails["metrics_count"] = manifest.Metrics.Count;
            }

            return Results.Ok(new
            {
                success = true,
                modelsCount = manifest.SemanticModels.Count,
                metricsCount = manifest.Metrics.Count
            });
        }
        catch (GatewayForbiddenException ex)
        {
            LogFailure(context, ex, "FORBIDDEN");
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (SemanticManifestPartialException ex)
        {
            LogFailure(context, ex, "DBT_MANIFEST_PARTIAL");
            return requestContext.Problem(
                StatusCodes.Status500InternalServerError,
                "DBT_MANIFEST_PARTIAL",
                "The semantic manifest was stored but its catalog tables were not fully updated; import it again.",
                context);
        }
        catch (Exception ex)
        {
            LogFailure(context, ex, "DBT_MANIFEST_INVALID");
            return requestContext.BadRequest("DBT_MANIFEST_INVALID", "The semantic manifest could not be processed.", context);
        }
    }

    internal static async Task<IResult> HandleListDbtMetricsAsync(
        HttpContext context,
        IModuleRequestContext requestContext,
        [FromServices] ISemanticCatalogService semanticCatalog,
        [FromServices] SemanticTenancy semanticTenancy)
    {
        var dataset = context.Request.Query["dataset"].ToString();
        var tenant = semanticTenancy.ResolveCallerTenant(context.User);
        IReadOnlyList<DbtMetric> metrics = tenant is null
            ? []
            : await semanticCatalog.ListMetricsAsync(
                string.IsNullOrWhiteSpace(dataset) ? null : dataset,
                requestContext.GetRequestTenant(context),
                context.RequestAborted);
        return Results.Ok(SemanticMetricCatalogProjection.Project(metrics, context.User));
    }
}
