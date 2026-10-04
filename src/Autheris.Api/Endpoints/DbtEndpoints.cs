namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Middleware;
using Autheris.Application.Dbt.Interfaces;
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
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("DataOwner") ||
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

            return await EndpointSecurity.WithBodyLimitAsync(async () =>
            {
                var result = await dbtService.IngestManifestStreamAsync(context.Request.Body, dryRun, context.RequestAborted);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            }, manifestTooLarge);
        }).RequireAuthorization()
          .WithRequestBodyLimit(100 * 1024 * 1024); // SEC M-01: explicit large-body exception to the global Kestrel limit

        app.MapGet("/api/extensions/dbt/exposures", async (
            IDbtExposurePublisher exposurePublisher,
            HttpContext context) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("DataOwner");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var yaml = await exposurePublisher.GenerateExposuresYamlAsync(context.RequestAborted);
            return Results.Content(yaml, "text/yaml; charset=utf-8");
        }).RequireAuthorization();

        app.MapGet("/api/extensions/dbt/proposals", async (
            IDbtMetadataIngestionService dbtService,
            HttpContext context) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("DataOwner");
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
        }).RequireAuthorization();

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
        }).RequireAuthorization();

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
        }).RequireAuthorization();

        app.MapPost("/api/extensions/dbt/validate-contract", async (
            HttpContext context,
            IDbtContractValidator validator) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("DataOwner") ||
                               context.User.IsInRole("Developer");
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
          .WithRequestBodyLimit(100 * 1024 * 1024); // SEC M-01: explicit large-body exception to the global Kestrel limit

        // dbt Health & Circuit Breaker Endpoints (F-DBT-1)
        app.MapPost("/api/extensions/dbt/run-results", async (
            HttpContext context,
            IDbtHealthCircuitBreaker circuitBreaker) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("DataOwner");
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
                var report = await circuitBreaker.RecordRunResultsAsync(context.Request.Body, context.RequestAborted);
                return Results.Ok(report);
            }, runResultsTooLarge);
        }).RequireAuthorization()
          .WithRequestBodyLimit(50 * 1024 * 1024); // SEC M-01: explicit large-body exception to the global Kestrel limit

        app.MapGet("/api/extensions/dbt/health", async (
            HttpContext context,
            IDbtHealthCircuitBreaker circuitBreaker) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("DataOwner") ||
                               context.User.IsInRole("Developer");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (context.Request.Query.TryGetValue("table", out var tableName) && !string.IsNullOrWhiteSpace(tableName))
            {
                var db = context.Request.Query.TryGetValue("database", out var dbVal) && !string.IsNullOrWhiteSpace(dbVal) ? dbVal.ToString() : "default";
                var schema = context.Request.Query.TryGetValue("schema", out var schemaVal) && !string.IsNullOrWhiteSpace(schemaVal) ? schemaVal.ToString() : "default";
                var tableId = new TableIdentifier(db, schema, tableName!);
                var health = await circuitBreaker.GetTableHealthAsync(tableId, context.RequestAborted);
                return Results.Ok(health);
            }

            var allStates = await circuitBreaker.GetAllHealthStatesAsync(context.RequestAborted);
            return Results.Ok(allStates);
        }).RequireAuthorization();

        app.MapPost("/api/extensions/dbt/health/reset", async (
            HttpContext context,
            IDbtHealthCircuitBreaker circuitBreaker) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("DataOwner");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (context.Request.Query.TryGetValue("table", out var tableName) && !string.IsNullOrWhiteSpace(tableName))
            {
                var db = context.Request.Query.TryGetValue("database", out var dbVal) && !string.IsNullOrWhiteSpace(dbVal) ? dbVal.ToString() : "default";
                var schema = context.Request.Query.TryGetValue("schema", out var schemaVal) && !string.IsNullOrWhiteSpace(schemaVal) ? schemaVal.ToString() : "default";
                var tableId = new TableIdentifier(db, schema, tableName!);
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
        }).RequireAuthorization();

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
          .WithRequestBodyLimit(10 * 1024 * 1024); // SEC M-01: explicit large-body exception to the global Kestrel limit

        return app;
    }
}
