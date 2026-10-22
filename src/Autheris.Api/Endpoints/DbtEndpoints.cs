namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Api.Endpoints.Support;
using Autheris.Api.Extensions;
using Autheris.Api.Middleware;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class DbtEndpoints
{
    public static IEndpointRouteBuilder MapDbtEndpoints(this IEndpointRouteBuilder app)
    {
        // dbt Ingestion & Exposure Endpoints (F-DATA-11)
        app.MapPost("/api/extensions/dbt/sync", async (
            HttpContext context,
            IDbtMetadataIngestionService dbtService) =>
        {
            // Review E-7: this endpoint changes global state (metadata, quarantine); a DataOwner role is not enough because
            // it carries no table ownership here. Only global governance administrators or the dedicated DbtAdmin role.
            var isPrivileged = EndpointSecurity.IsGlobalGovernanceAdmin(context.User) ||
                               context.User.IsInRole("DbtAdmin");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // SEC M-07: Server-side body limit instead of a bypassable Content-Length check.
            const string manifestTooLarge = "Manifest size exceeds maximum allowed size (100 MB).";
            var limitError = EndpointSecurity.TryApplyBodyLimit(context.Request, 100 * 1024 * 1024, manifestTooLarge);
            if (limitError != null)
            {
                return limitError;
            }

            var dryRun = context.Request.Query.ContainsKey("dryRun") &&
                         bool.TryParse(context.Request.Query["dryRun"], out var dr) && dr;

            Autheris.Domain.Security.SecurityPrincipalContext security;
            try
            {
                security = EndpointSecurity.GetSecurityContext(context);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var caller = new DbtGovernanceImportCaller(
                new VirtualFilterActor(security.UserSid, IsSync: false, context.User?.GetUserIdentifiers()),
                EndpointSecurity.GetRequestTenant(context),
                EndpointSecurity.IsCanonicalClusterAdmin(context.User));

            return await EndpointSecurity.WithBodyLimitAsync(async () =>
            {
                var result = await dbtService.IngestManifestStreamAsync(context.Request.Body, caller, dryRun, context.RequestAborted);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            }, manifestTooLarge);
        }).RequireAuthorization()
          .WithRequestBodyLimit(100 * 1024 * 1024) // SEC M-01: explicit large-body exception to the global Kestrel limit
          .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        // dbt Governance Ingestion Endpoint (R-24)
        app.MapPost("/api/extensions/dbt/governance", async (
            HttpContext context,
            IDbtMetadataIngestionService dbtService) =>
        {
            // The direct governance import writes masking rules, sensitivities, virtual filters and access profiles; the DbtAdmin
            // role alone (manifest / run-results automation) does not authorize it. Only global governance administrators.
            if (!EndpointSecurity.IsGlobalGovernanceAdmin(context.User))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // Virtual filters and access profiles are saved as the calling user (never as the sync, so the approval workflow
            // applies) in the server-resolved request tenant; only a canonical ClusterAdmin may import into another tenant.
            Autheris.Domain.Security.SecurityPrincipalContext security;
            try
            {
                security = EndpointSecurity.GetSecurityContext(context);
            }
            catch (UnauthorizedAccessException)
            {
                // Authenticated without a resolvable SID: refused (403) instead of surfacing as a 500.
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var caller = new DbtGovernanceImportCaller(
                new VirtualFilterActor(security.UserSid, IsSync: false, context.User?.GetUserIdentifiers()),
                EndpointSecurity.GetRequestTenant(context),
                EndpointSecurity.IsCanonicalClusterAdmin(context.User));

            const string governanceTooLarge = "Governance file size exceeds maximum allowed size (100 MB).";
            var limitError = EndpointSecurity.TryApplyBodyLimit(context.Request, 100 * 1024 * 1024, governanceTooLarge);
            if (limitError != null)
            {
                return limitError;
            }

            var dryRun = context.Request.Query.ContainsKey("dryRun") &&
                         bool.TryParse(context.Request.Query["dryRun"], out var dr) && dr;
            var replace = (context.Request.Query.ContainsKey("replace") &&
                           bool.TryParse(context.Request.Query["replace"], out var rep) && rep) ||
                          string.Equals(context.Request.Query["mode"], "replace", StringComparison.OrdinalIgnoreCase);

            return await EndpointSecurity.WithBodyLimitAsync(async () =>
            {
                var result = await dbtService.IngestGovernanceStreamAsync(context.Request.Body, caller, dryRun, replace, context.RequestAborted);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            }, governanceTooLarge);
        }).RequireAuthorization()
          .WithRequestBodyLimit(100 * 1024 * 1024)
          .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        app.MapGet("/api/extensions/dbt/exposures", async (
            IDbtExposurePublisher exposurePublisher,
            HttpContext context) =>
        {
            // Review E-7: dbt state is global and carries no table ownership; a plain DataOwner role may not read it.
            var isPrivileged = EndpointSecurity.IsGlobalGovernanceAdmin(context.User);
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var yaml = await exposurePublisher.GenerateExposuresYamlAsync(context.RequestAborted);
            return Results.Content(yaml, "text/yaml; charset=utf-8");
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapGet("/api/extensions/dbt/proposals", async (
            IDbtMetadataIngestionService dbtService,
            HttpContext context) =>
        {
            // Review E-7: dbt state is global and carries no table ownership; a plain DataOwner role may not read it.
            var isPrivileged = EndpointSecurity.IsGlobalGovernanceAdmin(context.User);
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
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapPost("/api/extensions/dbt/proposals/{id:guid}/approve", async (
            Guid id,
            IDbtMetadataIngestionService dbtService,
            HttpContext context) =>
        {
            // SEC M-11: dbt proposals carry no tenant and change global masking metadata ->
            // only global governance administrators (GovernanceAdmin / ClusterAdmin) may decide.
            if (!EndpointSecurity.IsGlobalGovernanceAdmin(context.User))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var user = context.User.Identity?.Name ?? context.User.GetUserSid()?.Value ?? "system_admin";
            try
            {
                var approved = await dbtService.ApproveProposalAsync(id, user, context.RequestAborted);
                return Results.Ok(approved);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Proposal '{id}' not found." });
            }
            catch (InvalidOperationException ex)
            {
                // R-EXT-1: not pending, not applicable (RLS/Casbin) or blocked by the ratchet; the status is unchanged.
                SafeErrorResults.LogFailure(context, ex, "DBT_PROPOSAL_CONFLICT");
                return SafeErrorResults.Conflict("DBT_PROPOSAL_CONFLICT", "The proposal cannot be approved in its current state.", context);
            }
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);

        app.MapPost("/api/extensions/dbt/proposals/{id:guid}/reject", async (
            Guid id,
            IDbtMetadataIngestionService dbtService,
            HttpContext context) =>
        {
            // SEC M-11: dbt proposals carry no tenant and change global masking metadata ->
            // only global governance administrators (GovernanceAdmin / ClusterAdmin) may decide.
            if (!EndpointSecurity.IsGlobalGovernanceAdmin(context.User))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var user = context.User.Identity?.Name ?? context.User.GetUserSid()?.Value ?? "system_admin";
            try
            {
                var rejected = await dbtService.RejectProposalAsync(id, user, context.RequestAborted);
                return Results.Ok(rejected);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Proposal '{id}' not found." });
            }
            catch (InvalidOperationException ex)
            {
                SafeErrorResults.LogFailure(context, ex, "DBT_PROPOSAL_CONFLICT");
                return SafeErrorResults.Conflict("DBT_PROPOSAL_CONFLICT", "The proposal cannot be rejected in its current state.", context);
            }
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.ConsentDenied);

        app.MapPost("/api/extensions/dbt/validate-contract", async (
            HttpContext context,
            IDbtContractValidator validator) =>
        {
            // Review E-7 / SG-13: dbt state is global and carries no table ownership; only global governance administrators or DbtAdmin.
            var isPrivileged = EndpointSecurity.IsGlobalGovernanceAdmin(context.User) ||
                               context.User.IsInRole("DbtAdmin");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // SEC M-07: Server-side body limit instead of a bypassable Content-Length check.
            const string manifestTooLarge = "Manifest size exceeds maximum allowed size (100 MB).";
            var limitError = EndpointSecurity.TryApplyBodyLimit(context.Request, 100 * 1024 * 1024, manifestTooLarge);
            if (limitError != null)
            {
                return limitError;
            }

            return await EndpointSecurity.WithBodyLimitAsync(async () =>
            {
                var result = await validator.ValidateContractsStreamAsync(context.Request.Body, context.RequestAborted);
                return result.IsCompatible ? Results.Ok(result) : Results.UnprocessableEntity(result);
            }, manifestTooLarge);
        }).RequireAuthorization()
          .WithRequestBodyLimit(100 * 1024 * 1024) // SEC M-01: explicit large-body exception to the global Kestrel limit
          .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // dbt Health & Circuit Breaker Endpoints (F-DBT-1)
        app.MapPost("/api/extensions/dbt/run-results", async (
            HttpContext context,
            IDbtHealthCircuitBreaker circuitBreaker) =>
        {
            // Review E-7: this endpoint changes global state (metadata, quarantine); a DataOwner role is not enough because
            // it carries no table ownership here. Only global governance administrators or the dedicated DbtAdmin role.
            var isPrivileged = EndpointSecurity.IsGlobalGovernanceAdmin(context.User) ||
                               context.User.IsInRole("DbtAdmin");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // SEC M-07: Server-side body limit instead of a bypassable Content-Length check.
            const string runResultsTooLarge = "Run results payload exceeds maximum allowed size (50 MB).";
            var limitError = EndpointSecurity.TryApplyBodyLimit(context.Request, 50 * 1024 * 1024, runResultsTooLarge);
            if (limitError != null)
            {
                return limitError;
            }

            return await EndpointSecurity.WithBodyLimitAsync(async () =>
            {
                var tenant = EndpointSecurity.GetRequestTenant(context);
                var report = await circuitBreaker.RecordRunResultsAsync(context.Request.Body, tenant, context.RequestAborted);
                return Results.Ok(report);
            }, runResultsTooLarge);
        }).RequireAuthorization()
          .WithRequestBodyLimit(50 * 1024 * 1024) // SEC M-01: explicit large-body exception to the global Kestrel limit
          .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        app.MapGet("/api/extensions/dbt/health", async (
            HttpContext context,
            IDbtHealthCircuitBreaker circuitBreaker,
            IDbtTestTargetMap? testTargetMap) =>
        {
            // Review E-7 / SG-13: dbt state is global and carries no table ownership; only global governance administrators or DbtAdmin.
            var isPrivileged = EndpointSecurity.IsGlobalGovernanceAdmin(context.User) ||
                               context.User.IsInRole("DbtAdmin");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (context.Request.Query.TryGetValue("table", out var tableName) && !string.IsNullOrWhiteSpace(tableName))
            {
                var reqTenant = EndpointSecurity.GetRequestTenant(context);
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
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapPost("/api/extensions/dbt/health/reset", async (
            HttpContext context,
            IDbtHealthCircuitBreaker circuitBreaker,
            IDbtTestTargetMap? testTargetMap) =>
        {
            // Review E-7: this endpoint changes global state (metadata, quarantine); a DataOwner role is not enough because
            // it carries no table ownership here. Only global governance administrators or the dedicated DbtAdmin role.
            var isPrivileged = EndpointSecurity.IsGlobalGovernanceAdmin(context.User) ||
                               context.User.IsInRole("DbtAdmin");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (context.Request.Query.TryGetValue("table", out var tableName) && !string.IsNullOrWhiteSpace(tableName))
            {
                var reqTenant = EndpointSecurity.GetRequestTenant(context);
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

            // SEC M-11: Resetting all health states is a global action -> GovernanceAdmin / ClusterAdmin only.
            if (!EndpointSecurity.IsGlobalGovernanceAdmin(context.User))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            await circuitBreaker.ResetAllAsync(context.RequestAborted);
            return Results.Ok(new { message = "All table health states reset to healthy." });
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        // F-DBT-4: dbt Cloud & Orchestrator HMAC Webhook Receiver
        app.MapPost("/api/extensions/dbt/webhooks/dbt-cloud", async (
            HttpContext context,
            IDbtWebhookReceiver webhookReceiver) =>
        {
            // SEC M-07: Bounded read instead of a bypassable Content-Length check.
            var (payload, tooLarge) = await EndpointSecurity.TryReadBodyAsync(
                context.Request,
                10 * 1024 * 1024,
                "Webhook payload exceeds maximum allowed size (10 MB).",
                context.RequestAborted);
            if (payload == null)
            {
                return tooLarge!;
            }

            string? signatureHeader = null;
            if (context.Request.Headers.TryGetValue("X-Dbt-Signature", out var dbtSig))
            {
                signatureHeader = dbtSig.ToString();
            }
            else if (context.Request.Headers.TryGetValue("X-Hub-Signature-256", out var hubSig))
            {
                signatureHeader = hubSig.ToString();
            }

            var result = await webhookReceiver.ProcessWebhookAsync(payload, signatureHeader, context.RequestAborted);
            if (!result.Success)
            {
                return Results.Json(result, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(result);
        }).AllowAnonymous()
          .WithRequestBodyLimit(10 * 1024 * 1024) // SEC M-01: explicit large-body exception to the global Kestrel limit
          .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        // Wave 4 / Issue #38: dbt Semantic Manifest Ingestion & Metrics Catalog
        app.MapPost("/api/extensions/dbt/semantic-manifest", HandleImportSemanticManifestAsync)
          .RequireAuthorization()
          .WithRequestBodyLimit(10 * 1024 * 1024)
          .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        app.MapGet("/api/extensions/dbt/metrics", HandleListDbtMetricsAsync)
          .RequireAuthorization()
          .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        return app;
    }

    internal static async Task<IResult> HandleImportSemanticManifestAsync(
        HttpContext context,
        IDbtSemanticManifestIngestionService ingestionService,
        SemanticTenancy semanticTenancy)
    {
        var isPrivileged = EndpointSecurity.IsGlobalGovernanceAdmin(context.User) ||
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

        var (payload, tooLarge) = await EndpointSecurity.TryReadBodyAsync(
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
                EndpointSecurity.GetRequestTenant(context),
                EndpointSecurity.GetCallerIdentity(context),
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
            SafeErrorResults.LogFailure(context, ex, "FORBIDDEN");
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (SemanticManifestPartialException ex)
        {
            SafeErrorResults.LogFailure(context, ex, "DBT_MANIFEST_PARTIAL");
            return SafeErrorResults.Problem(
                StatusCodes.Status500InternalServerError,
                "DBT_MANIFEST_PARTIAL",
                "The semantic manifest was stored but its catalog tables were not fully updated; import it again.",
                context);
        }
        catch (Exception ex)
        {
            SafeErrorResults.LogFailure(context, ex, "DBT_MANIFEST_INVALID");
            return SafeErrorResults.BadRequest("DBT_MANIFEST_INVALID", "The semantic manifest could not be processed.", context);
        }
    }

    internal static async Task<IResult> HandleListDbtMetricsAsync(
        HttpContext context,
        ISemanticCatalogService semanticCatalog,
        SemanticTenancy semanticTenancy)
    {
        var dataset = context.Request.Query["dataset"].ToString();
        var tenant = semanticTenancy.ResolveCallerTenant(context.User);
        IReadOnlyList<DbtMetric> metrics = tenant is null
            ? []
            : await semanticCatalog.ListMetricsAsync(
                string.IsNullOrWhiteSpace(dataset) ? null : dataset,
                EndpointSecurity.GetRequestTenant(context),
                context.RequestAborted);
        return Results.Ok(SemanticMetricCatalogProjection.Project(metrics, context.User));
    }
}
