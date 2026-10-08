namespace Autheris.Api.Endpoints;

using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class GovernanceEndpoints
{
    internal static async Task<bool> IsAuthorizedForSimulationAsync(HttpContext context, string? targetTable)
    {
        var user = context.User;
        if (user.IsInRole("PrivacyAdmin") || GatewayPolicies.HasAnyRole(user, [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin, GatewayRole.SecurityAuditor]))
        {
            return true;
        }

        if (!GatewayPolicies.HasRole(user, GatewayRole.DataOwner) ||
            string.IsNullOrWhiteSpace(targetTable) ||
            !TableIdentifier.TryParse(targetTable, out var table))
        {
            return false;
        }

        var sid = EndpointSecurity.GetSecurityContext(context)?.UserSid.Value ?? user.GetUserSid()?.Value;
        var ownershipRepository = context.RequestServices.GetService(typeof(IDataOwnershipRepository)) as IDataOwnershipRepository;
        if (string.IsNullOrWhiteSpace(sid) || ownershipRepository == null)
        {
            return false;
        }

        return await ownershipRepository.IsAuthorizedApproverForTableAsync(table, new Sid(sid), context.RequestAborted).ConfigureAwait(false);
    }

    public static IEndpointRouteBuilder MapGovernanceEndpoints(this IEndpointRouteBuilder app)
    {
        // F-API-04: Declarative Web API OpenAPI/Swagger Schema & Doc Ingestion
        app.MapPost("/api/governance/catalog/ingest-openapi", async (
            HttpContext context,
            IOpenApiIngestionService ingestionService) =>
        {
            var isPrivileged = GatewayPolicies.HasAnyRole(context.User, [GatewayRole.GovernanceAdmin, GatewayRole.SchemaPublisher]);
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var domain = context.Request.Query.TryGetValue("domain", out var dVal) && !string.IsNullOrWhiteSpace(dVal)
                ? dVal.ToString()
                : "external";
            var baseUrl = context.Request.Query.TryGetValue("baseUrl", out var bVal) && !string.IsNullOrWhiteSpace(bVal)
                ? bVal.ToString()
                : null;

            // SEC M-07: Bounded read instead of a bypassable Content-Length check.
            var (json, tooLarge) = await EndpointSecurity.TryReadBodyAsync(
                context.Request,
                20 * 1024 * 1024,
                "OpenAPI specification exceeds maximum allowed size (20 MB).",
                context.RequestAborted);
            if (json == null)
            {
                return tooLarge!;
            }

            var result = await ingestionService.IngestOpenApiJsonAsync(json, domain, baseUrl, context.RequestAborted);
            if (!result.Success)
            {
                return Results.BadRequest(result);
            }

            return Results.Ok(result);
        }).RequireAuthorization()
          .WithRequestBodyLimit(20 * 1024 * 1024); // SEC M-01: explicit large-body exception to the global Kestrel limit

        // P10: Multi-Tenant Policy Simulation Sandbox ("What-If" Replay via Audit Logs)
        app.MapPost("/api/governance/policy-simulation/replay", async (
            PolicySimulationRequest request,
            IPolicySimulationService simulationService,
            HttpContext context) =>
        {
            // A-2: the replay exposes the tenant audit trail (ActorSid, TargetTable, ...). Only audit/governance roles
            // may replay tenant-wide; a DataOwner only for one concrete table they own or are delegate of.
            if (!await IsAuthorizedForSimulationAsync(context, request.TargetTable).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // SEC H-05: Tenant is enforced server-side from the resolved principal tenant;
            // a foreign tenant from the request body is honored only for ClusterAdmin.
            var effectiveTenant = context.Items.TryGetValue(TenantResolutionMiddleware.TenantIdItemKey, out var itemTenant) && itemTenant is TenantId resolvedTenant
                ? resolvedTenant
                : TenantId.LegacySingleTenant;
            if (request.Tenant is { } requestedTenant && requestedTenant != effectiveTenant)
            {
                if (!GatewayPolicies.HasRole(context.User, GatewayRole.ClusterAdmin))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
                effectiveTenant = requestedTenant;
            }

            var result = await simulationService.SimulateAsync(request, effectiveTenant, context.RequestAborted);
            return Results.Ok(result);
        }).RequireAuthorization();

        // P11: Automated Schema Deprecation & Client-Impact Sunsetting (Smart Sunsetting Engine)
        app.MapGet("/api/governance/sunsetting/rules", async (
            ISchemaSunsettingService sunsettingService,
            HttpContext context) =>
        {
            var isPrivileged = GatewayPolicies.HasAnyRole(context.User, [GatewayRole.GovernanceAdmin, GatewayRole.SchemaPublisher, GatewayRole.DataOwner]);
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var rules = await sunsettingService.GetRulesAsync(context.RequestAborted);
            return Results.Ok(rules);
        }).RequireAuthorization();

        app.MapPost("/api/governance/sunsetting/rules", async (
            FieldSunsettingRule rule,
            ISchemaSunsettingService sunsettingService,
            HttpContext context) =>
        {
            // SEC M-11: Sunsetting rules are global (not tenant-scoped) -> GovernanceAdmin / ClusterAdmin only.
            if (!EndpointSecurity.IsGlobalGovernanceAdmin(context.User))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            await sunsettingService.RegisterRuleAsync(rule, context.RequestAborted);
            return Results.Created($"/api/governance/sunsetting/rules/{rule.Id}", rule);
        }).RequireAuthorization();

        app.MapPost("/api/governance/sunsetting/evaluate", async (
            EvaluateFieldSunsettingRequest request,
            ISchemaSunsettingService sunsettingService,
            HttpContext context) =>
        {
            var isPrivileged = GatewayPolicies.HasAnyRole(context.User, [GatewayRole.GovernanceAdmin, GatewayRole.SchemaPublisher, GatewayRole.DataOwner, GatewayRole.Consumer]);
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var evaluation = await sunsettingService.EvaluateFieldAsync(
                request.TargetTable,
                request.FieldName,
                request.EvaluationDate,
                context.RequestAborted);

            if (evaluation == null)
            {
                return Results.Ok(new { isDeprecated = false });
            }

            if (evaluation.IsHardSunsetBlocked)
            {
                context.Response.Headers["Sunset"] = evaluation.HttpSunsetHeader;
                return Results.Json(new
                {
                    error = evaluation.DeprecationNotice,
                    phase = evaluation.Phase.ToString(),
                    sunsetDate = evaluation.SunsetDate,
                    replacement = evaluation.Rule.ReplacementField
                }, statusCode: StatusCodes.Status410Gone);
            }

            if (evaluation.ShouldInjectSyntheticLatency && evaluation.SyntheticLatencyMs > 0)
            {
                await Task.Delay(evaluation.SyntheticLatencyMs, context.RequestAborted);
            }

            if (evaluation.ShouldRejectWith426)
            {
                context.Response.Headers["Sunset"] = evaluation.HttpSunsetHeader;
                return Results.Json(new
                {
                    error = "Chaos Testing: Upgrade Required. " + evaluation.DeprecationNotice,
                    phase = evaluation.Phase.ToString(),
                    sunsetDate = evaluation.SunsetDate,
                    replacement = evaluation.Rule.ReplacementField
                }, statusCode: StatusCodes.Status426UpgradeRequired);
            }

            context.Response.Headers["Sunset"] = evaluation.HttpSunsetHeader;
            return Results.Ok(evaluation);
        }).RequireAuthorization();

        // P12: Federated Differential Privacy & Dynamic Epsilon-Perturbation Engine
        app.MapGet("/api/governance/differential-privacy/budget/{clientId}", async (
            string clientId,
            IDifferentialPrivacyEngine dpEngine,
            HttpContext context) =>
        {
            var authenticatedClientId = context.User.FindFirst("client_id")?.Value
                                        ?? context.User.FindFirst("azp")?.Value
                                        ?? context.User.FindFirst("sub")?.Value
                                        ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                                        ?? context.User.Identity?.Name;

            var isPrivileged = GatewayPolicies.HasAnyRole(context.User, [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin, GatewayRole.SecurityAuditor]);

            var targetKey = ResolveTenantBoundClientId(context, clientId, out var isForbidden);
            if (isForbidden)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (!isPrivileged && !string.Equals(clientId, authenticatedClientId, StringComparison.OrdinalIgnoreCase))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var budget = await dpEngine.GetBudgetAsync(targetKey, context.RequestAborted);
            return Results.Ok(budget);
        }).RequireAuthorization();

        app.MapPost("/api/governance/differential-privacy/budget/{clientId}/reset", async (
            string clientId,
            IDifferentialPrivacyEngine dpEngine,
            HttpContext context) =>
        {
            var isCanonicalClusterAdmin = EndpointSecurity.IsCanonicalClusterAdmin(context.User);
            var isPrivileged = isCanonicalClusterAdmin ||
                               GatewayPolicies.HasAnyRole(context.User, [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin]);
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // SEC M-4: Non-canonical cluster admins may only reset clients within their tenant scope
            var targetKey = ResolveTenantBoundClientId(context, clientId, out var isForbidden);
            if (isForbidden)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            await dpEngine.ResetBudgetAsync(targetKey, context.RequestAborted);
            return Results.Ok(new { message = $"Privacy budget reset for client '{clientId}'." });
        }).RequireAuthorization();

        app.MapPost("/api/governance/differential-privacy/perturb", async (
            DifferentialPrivacyPerturbationRequest request,
            IDifferentialPrivacyEngine dpEngine,
            HttpContext context) =>
        {
            try
            {
                var authenticatedClientId = context.User.FindFirst("client_id")?.Value
                                            ?? context.User.FindFirst("azp")?.Value
                                            ?? context.User.FindFirst("sub")?.Value
                                            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                                            ?? context.User.Identity?.Name;

                var isPrivileged = GatewayPolicies.HasAnyRole(context.User, [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin, GatewayRole.SecurityAuditor]);

                if (!isPrivileged || string.IsNullOrWhiteSpace(request.ClientId))
                {
                    if (string.IsNullOrWhiteSpace(authenticatedClientId))
                    {
                        return Results.Unauthorized();
                    }
                    request = request with { ClientId = authenticatedClientId };
                }

                var targetKey = ResolveTenantBoundClientId(context, request.ClientId, out var isForbidden);
                if (isForbidden)
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                var boundRequest = request with { ClientId = targetKey };
                var result = await dpEngine.PerturbAsync(boundRequest, context.RequestAborted);
                context.Response.Headers["X-Privacy-Budget-Consumed"] = result.ConsumedEpsilon.ToString("F2");
                context.Response.Headers["X-Privacy-Budget-Remaining"] = result.RemainingEpsilon.ToString("F2");
                return Results.Ok(result with { ClientId = request.ClientId });
            }
            catch (PrivacyBudgetExhaustedException ex)
            {
                context.Response.Headers["X-Privacy-Budget-Exhausted"] = "true";
                return Results.Json(new
                {
                    error = ex.Message,
                    clientId = ex.ClientId,
                    consumed = ex.ConsumedEpsilon,
                    totalBudget = ex.TotalBudget
                }, statusCode: StatusCodes.Status429TooManyRequests);
            }
        }).RequireAuthorization();

        // F-AI-12-B: EU AI Act Article 10 Compliance Certificate Endpoint
        app.MapGet("/api/governance/eu-ai-act/article-10-certificate", async (
            IEuAiActAuditExporter exporter,
            HttpContext context) =>
        {
            var isPrivileged = GatewayPolicies.HasAnyRole(context.User, [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin, GatewayRole.SecurityAuditor]);

            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var isCanonicalClusterAdmin = EndpointSecurity.IsCanonicalClusterAdmin(context.User);
            var tenantId = context.Request.Query["tenantId"].ToString();
            if (!isCanonicalClusterAdmin || string.IsNullOrWhiteSpace(tenantId))
            {
                var reqTenant = EndpointSecurity.GetRequestTenant(context);
                tenantId = !string.IsNullOrWhiteSpace(reqTenant.Value) ? reqTenant.Value : "default";
            }

            var cert = await exporter.GenerateCertificateAsync(tenantId, context.RequestAborted);
            context.Response.Headers["X-Certificate-Id"] = cert.CertificateId;
            context.Response.Headers["X-Integrity-Seal"] = cert.IntegritySealSha256;
            return Results.Ok(cert);
        }).RequireAuthorization();

        // GDPR Article 15 PDF Export for Data Protection Officers (DSB)
        app.MapGet("/api/governance/gdpr/export-pdf", async (
            string? domain,
            string? schema,
            string? table,
            string? subjectSid,
            int? timeWindowDays,
            ILineageImpactAnalyzerService lineageService,
            IGdprAuditReportExporter pdfExporter,
            HttpContext context,
            CancellationToken ct) =>
        {
            var principal = context.User;
            var userSid = principal.GetUserSid() ?? new Sid("S-1-5-21-ANONYMOUS");
            var groupSids = principal.GetGroupSids().ToList();
            var roles = principal.GetUserRoles().ToList();

            var tenantId = TenantId.LegacySingleTenant;
            if (context.Items.TryGetValue("TenantId", out var tidObj) == true && tidObj is TenantId tid)
            {
                tenantId = tid;
            }

            bool isGovAdmin = roles.Contains("GovernanceAdmin", StringComparer.OrdinalIgnoreCase);
            bool isClusterAdmin = roles.Contains("ClusterAdmin", StringComparer.OrdinalIgnoreCase);
            bool isPrivacyAdmin = roles.Contains("PrivacyAdmin", StringComparer.OrdinalIgnoreCase) ||
                                  roles.Contains("DataProtectionOfficer", StringComparer.OrdinalIgnoreCase);

            var callerContext = new CallerSecurityContext(
                userSid,
                groupSids,
                roles,
                tenantId,
                isGovAdmin,
                isClusterAdmin);

            TableIdentifier? tableId = !string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(schema) && !string.IsNullOrWhiteSpace(table)
                ? new TableIdentifier(domain, schema, table)
                : null;
            Sid? sid = !string.IsNullOrWhiteSpace(subjectSid) ? new Sid(subjectSid) : (Sid?)null;

            bool canAccessForeignReports = isPrivacyAdmin || isGovAdmin || isClusterAdmin;
            var effectiveSid = sid ?? (canAccessForeignReports ? (Sid?)null : callerContext.UserSid);

            if (effectiveSid.HasValue && !effectiveSid.Value.Equals(callerContext.UserSid) && !canAccessForeignReports)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var report = await lineageService.GetGdprDataDisclosureReportAsync(tableId, effectiveSid, timeWindowDays ?? 365, callerContext, ct);
            var exportResult = pdfExporter.ExportReportToPdf(report);

            context.Response.Headers["X-Audit-Seal-SHA256"] = exportResult.Sha256AuditSeal;
            return Results.File(exportResult.DocumentBytes, exportResult.ContentType, exportResult.FileName);
        }).RequireAuthorization();

        // OpenLineage Lineage Push Trigger
        app.MapPost("/api/lineage/openlineage/sync", async (
            IOpenLineageClient openLineageClient,
            HttpContext context,
            CancellationToken ct) =>
        {
            var isPrivileged = GatewayPolicies.HasAnyRole(context.User, [GatewayRole.GovernanceAdmin, GatewayRole.DataOwner]);
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var tenantId = context.User.FindFirst("tenant_id")?.Value ?? "default";
            var success = await openLineageClient.PushLineageGraphAsync(new TenantId(tenantId), ct);
            return success
                ? Results.Ok(new { message = "OpenLineage sync completed successfully." })
                : Results.StatusCode(StatusCodes.Status502BadGateway);
        }).RequireAuthorization();

        return app;
    }

    private static string ResolveTenantBoundClientId(HttpContext context, string clientId, out bool isForbidden)
    {
        isForbidden = false;
        var reqTenant = EndpointSecurity.GetRequestTenant(context).Value;
        var isCanonicalClusterAdmin = EndpointSecurity.IsCanonicalClusterAdmin(context.User);

        var colonIdx = clientId.IndexOf(':');
        if (colonIdx > 0)
        {
            var targetTenant = clientId[..colonIdx];
            if (!isCanonicalClusterAdmin && !string.Equals(targetTenant, reqTenant, StringComparison.OrdinalIgnoreCase))
            {
                isForbidden = true;
                return clientId;
            }
            return clientId;
        }

        return string.IsNullOrWhiteSpace(reqTenant) ? clientId : $"{reqTenant}:{clientId}";
    }
}
