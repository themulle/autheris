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
using Autheris.Application.Policy;
using Autheris.Application.State;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using TrinoSqlEngine;

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

            var callerTenant = EndpointSecurity.GetRequestTenant(context);
            var isClusterAdmin = EndpointSecurity.IsCanonicalClusterAdmin(context.User);

            string domain;
            if (context.Request.Query.TryGetValue("domain", out var dVal) && !string.IsNullOrWhiteSpace(dVal))
            {
                var requestedDomain = dVal.ToString().Trim();
                if (!isClusterAdmin && !string.Equals(requestedDomain, callerTenant.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
                domain = requestedDomain;
            }
            else
            {
                domain = isClusterAdmin ? "external" : callerTenant.Value;
            }

            var baseUrl = context.Request.Query.TryGetValue("baseUrl", out var bVal) && !string.IsNullOrWhiteSpace(bVal)
                ? bVal.ToString().Trim()
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

            var auditRepo = context.RequestServices.GetService<IAuditLogRepository>();
            if (auditRepo != null)
            {
                await auditRepo.RecordAuditEventAsync(new AuditLogEntry
                {
                    TenantId = callerTenant,
                    EventType = "OPENAPI_CATALOG_INGESTED",
                    ActorSid = context.User.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
                    TargetTable = domain,
                    Decision = "ALLOW",
                    TraceId = context.TraceIdentifier,
                    DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        domain,
                        serviceTitle = result.ServiceTitle,
                        ingestedTablesCount = result.IngestedTablesCount,
                        ingestedColumnsCount = result.IngestedColumnsCount,
                        ingestedTableNames = result.IngestedTableNames,
                        warnings = result.Warnings
                    })
                }, context.RequestAborted).ConfigureAwait(false);
            }

            return Results.Ok(result);
        }).RequireAuthorization()
          .WithRequestBodyLimit(20 * 1024 * 1024) // SEC M-01: explicit large-body exception to the global Kestrel limit
          .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

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
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.AuditRead);

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
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

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
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

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
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.AuthSucceeded);

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
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

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

            var callerId = context.User.FindFirst("client_id")?.Value
                         ?? context.User.FindFirst("azp")?.Value
                         ?? context.User.FindFirst("sub")?.Value
                         ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                         ?? context.User.Identity?.Name;

            // SEC SG-29: Prevent self-reset of privacy budget
            if (!string.IsNullOrWhiteSpace(callerId) && string.Equals(callerId, clientId, StringComparison.OrdinalIgnoreCase))
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

            var auditRepo = context.RequestServices.GetService<IAuditLogRepository>();
            if (auditRepo != null)
            {
                await auditRepo.RecordAuditEventAsync(new AuditLogEntry
                {
                    TenantId = EndpointSecurity.GetRequestTenant(context),
                    EventType = AuditEventTypes.AuditConfigChanged,
                    ActorSid = context.User.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
                    TargetTable = clientId,
                    Decision = "ALLOW",
                    TraceId = context.TraceIdentifier,
                    DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        clientId,
                        targetKey,
                        resetBy = callerId
                    })
                }, context.RequestAborted).ConfigureAwait(false);
            }

            return Results.Ok(new { message = $"Privacy budget reset for client '{clientId}'." });
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

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

                var isPrivileged = GatewayPolicies.HasAnyRole(context.User, [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin]);

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
        }).RequireAuthorization()
          .WithAudit(AuditLevel.Full, AuditEventTypes.TableQuery);

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
        }).RequireAuthorization()
          .WithAudit(AuditLevel.Full, AuditEventTypes.MetadataExport);

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

            var tenantId = EndpointSecurity.GetRequestTenant(context);

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
        }).RequireAuthorization()
          .WithAudit(AuditLevel.Full, AuditEventTypes.MetadataExport);

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
            if (success)
            {
                return Results.Ok(new { message = "OpenLineage sync completed successfully." });
            }
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }).RequireAuthorization()
          .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);
        // =========================================================================
        // Declarative Access Profiles & Subject Cleartext Exceptions (R-52 & R-50)
        // =========================================================================

        app.MapPost("/api/v1/consents/bulk", CreateBulkConsentAsync).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);
        app.MapGet("/api/v1/profiles", GetProfilesAsync).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        app.MapGet("/api/v1/profiles/{id}", GetProfileByIdAsync).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        app.MapDelete("/api/v1/profiles/{id}", DeleteProfileAsync).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        return app;
    }

    internal static async Task<IResult> CreateBulkConsentAsync(
        BulkConsentRequest req,
        HttpContext context,
        IAccessProfileRepository accessProfileRepo,
        CancellationToken ct)
    {
        var isPrivileged = GatewayPolicies.HasAnyRole(context.User, [GatewayRole.ClusterAdmin, GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin])
            || context.User.IsInRole("SecurityAdmin")
            || context.User.IsInRole("ClusterAdmin");
        if (!isPrivileged)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (req == null)
        {
            return Results.BadRequest(new { error = "Request body is required." });
        }

        if (string.IsNullOrWhiteSpace(req.Subject))
        {
            return Results.BadRequest(new { error = "Subject is required." });
        }

        var trimmedSubject = req.Subject.Trim();
        var callerSid = context.User.GetUserSid()?.Value ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.User.Identity?.Name ?? string.Empty;
        var callerName = context.User.Identity?.Name ?? string.Empty;

        // Segregation of Duties (SoD): self-grant is strictly forbidden
        if (string.Equals(callerSid, trimmedSubject, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(callerName, trimmedSubject, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "Segregation of Duties (SoD) violation: Self-grant of access profiles is strictly prohibited." });
        }

        // Justification validation (minimum 15 characters)
        if (string.IsNullOrWhiteSpace(req.Justification) || req.Justification.Trim().Length < 15)
        {
            return Results.BadRequest(new { error = "Justification is required and must be at least 15 characters long." });
        }

        // ValidDays validation (1 to 180 days)
        if (req.ValidDays < 1 || req.ValidDays > 180)
        {
            return Results.BadRequest(new { error = "validDays must be between 1 and 180." });
        }

        // MaskingMode validation
        if (!Enum.TryParse<MaskingPolicyMode>(req.MaskingMode, ignoreCase: true, out var mode))
        {
            return Results.BadRequest(new { error = $"Invalid maskingMode '{req.MaskingMode}'. Supported values: Default, Unmasked, Strict." });
        }

        // Tables validation
        if (req.Tables == null || req.Tables.Count == 0)
        {
            return Results.BadRequest(new { error = "At least one target table pattern must be provided in 'tables'." });
        }

        // Overbroad wildcard restriction: *.* or * is forbidden for Unmasked
        if (mode == MaskingPolicyMode.Unmasked)
        {
            if (req.Tables.Any(t => string.IsNullOrWhiteSpace(t) || t.Trim() is "*.*" or "*"))
            {
                return Results.BadRequest(new { error = "Overbroad wildcard '*.*' is prohibited for Unmasked access profiles. Minimum schema scope required (e.g. 'tem.*')." });
            }
        }

        // RowFilter sandbox AST validation
        if (!string.IsNullOrWhiteSpace(req.RowFilter))
        {
            var trimmedFilter = req.RowFilter.Trim();
            if (trimmedFilter.Contains(';') || trimmedFilter.Contains("--") || trimmedFilter.Contains("/*") || trimmedFilter.Contains("*/"))
            {
                return Results.BadRequest(new { error = "Row filter predicate contains prohibited SQL constructs (semicolons or comments)." });
            }

            try
            {
                var parser = new FastSqlEngine();
                _ = parser.ParseExpression(trimmedFilter.AsMemory(), SqlTokenSecurityOptions.None, ct);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Invalid row filter syntax: {ex.Message}" });
            }
        }

        var tenantId = EndpointSecurity.GetRequestTenant(context);
        var now = DateTimeOffset.UtcNow;
        var validTo = now.AddDays(req.ValidDays);
        var profileId = $"prof-{trimmedSubject.ToLowerInvariant()}-{mode.ToString().ToLowerInvariant()}-{now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var targetTables = req.Tables.Select(t => t.Trim()).ToList();

        // Calculate matched tables
        int tablesMatched = 0;
        var metaRepo = context.RequestServices.GetService<ITableMetadataRepository>();
        if (metaRepo != null)
        {
            try
            {
                var allTables = await metaRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
                tablesMatched = allTables.Count(t => targetTables.Any(pattern => AccessProfile.MatchesPattern(pattern, t.Identifier)));
            }
            catch
            {
                // Fallback to table count if metadata repository query fails
                tablesMatched = targetTables.Count;
            }
        }
        else
        {
            tablesMatched = targetTables.Count;
        }

        var subjectType = string.IsNullOrWhiteSpace(req.SubjectType) ? "User" : req.SubjectType.Trim();
        var profile = new AccessProfile
        {
            ProfileId = profileId,
            TenantId = tenantId,
            Name = $"BulkConsent_{trimmedSubject}_{mode}",
            MaskingMode = mode,
            TargetTables = targetTables,
            RowFilterPredicate = string.IsNullOrWhiteSpace(req.RowFilter) ? null : req.RowFilter.Trim(),
            AssignedSubjects = [trimmedSubject],
            CreatedAt = now,
            ValidTo = validTo,
            Justification = req.Justification.Trim(),
            CreatedBy = callerSid
        };

        await accessProfileRepo.UpsertProfileAsync(profile, ct).ConfigureAwait(false);

        // Audit-Verankerung (Tier-A CONSENT_GRANTED)
        var auditEvent = new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = "CONSENT_GRANTED",
            ActorSid = context.User.GetUserSid() ?? new Sid(callerSid),
            TargetTable = string.Join(",", targetTables),
            Decision = "ALLOW",
            TraceId = context.TraceIdentifier,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                profileId,
                subject = trimmedSubject,
                subjectType,
                maskingMode = mode.ToString(),
                tables = targetTables,
                rowFilter = profile.RowFilterPredicate,
                validDays = req.ValidDays,
                validTo,
                justification = profile.Justification,
                tablesMatched
            })
        };

        var auditRepo = context.RequestServices.GetService<IAuditLogRepository>();
        if (auditRepo != null)
        {
            await auditRepo.RecordAuditEventAsync(auditEvent, ct).ConfigureAwait(false);
        }

        // Cluster-weite Cache-Invalidierung (AR-01)
        var accessProfileCache = context.RequestServices.GetService<Autheris.Application.Policy.Interfaces.IAccessProfileCache>();
        if (accessProfileCache != null)
        {
            try
            {
                await accessProfileCache.InvalidateTenantAsync(tenantId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (auditRepo != null)
                {
                    var failAudit = new AuditLogEntry
                    {
                        TenantId = tenantId,
                        EventType = "PROFILE_INVALIDATION_FAILED",
                        ActorSid = context.User.GetUserSid() ?? new Sid(callerSid),
                        Decision = "DENY",
                        TraceId = context.TraceIdentifier,
                        DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            reason = ex.Message,
                            tenant = tenantId.Value,
                            subject = trimmedSubject
                        })
                    };
                    await auditRepo.RecordAuditEventAsync(failAudit, ct).ConfigureAwait(false);
                }
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }
        else
        {
            var clusterState = context.RequestServices.GetService<IDistributedClusterStateProvider>();
            if (clusterState != null)
            {
                var bumped = await clusterState.IncrementAsync($"profile_epoch:{tenantId.Value}", 1, TimeSpan.Zero, ct).ConfigureAwait(false);
                if (!bumped.HasValue)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            }
        }

        var message = mode == MaskingPolicyMode.Unmasked
            ? "Bulk consent and unmasked access profile successfully created and activated."
            : "Bulk consent and access profile successfully created and activated.";

        return Results.Created($"/api/v1/profiles/{profileId}", new
        {
            profileId,
            subject = trimmedSubject,
            maskingMode = mode.ToString(),
            tablesMatched,
            rowFilter = profile.RowFilterPredicate,
            validTo,
            auditEventId = auditEvent.Id,
            message
        });
    }

    internal static async Task<IResult> GetProfilesAsync(
        HttpContext context,
        IAccessProfileRepository accessProfileRepo,
        CancellationToken ct)
    {
        var isPrivileged = GatewayPolicies.HasAnyRole(context.User, [GatewayRole.ClusterAdmin, GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin, GatewayRole.SecurityAuditor])
            || context.User.IsInRole("SecurityAdmin")
            || context.User.IsInRole("ClusterAdmin");
        if (!isPrivileged)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var tenantId = EndpointSecurity.GetRequestTenant(context);
        var profiles = await accessProfileRepo.GetAllProfilesAsync(tenantId, ct).ConfigureAwait(false);
        return Results.Ok(profiles);
    }

    internal static async Task<IResult> GetProfileByIdAsync(
        string id,
        HttpContext context,
        IAccessProfileRepository accessProfileRepo,
        CancellationToken ct)
    {
        var isPrivileged = GatewayPolicies.HasAnyRole(context.User, [GatewayRole.ClusterAdmin, GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin, GatewayRole.SecurityAuditor])
            || context.User.IsInRole("SecurityAdmin")
            || context.User.IsInRole("ClusterAdmin");
        if (!isPrivileged)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var tenantId = EndpointSecurity.GetRequestTenant(context);
        var profile = await accessProfileRepo.GetProfileAsync(tenantId, id, ct).ConfigureAwait(false);
        if (profile == null)
        {
            return Results.NotFound(new { error = $"Profile '{id}' not found." });
        }

        return Results.Ok(profile);
    }

    internal static async Task<IResult> DeleteProfileAsync(
        string id,
        HttpContext context,
        IAccessProfileRepository accessProfileRepo,
        CancellationToken ct)
    {
        var isPrivileged = GatewayPolicies.HasAnyRole(context.User, [GatewayRole.ClusterAdmin, GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin])
            || context.User.IsInRole("SecurityAdmin")
            || context.User.IsInRole("ClusterAdmin");
        if (!isPrivileged)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var tenantId = EndpointSecurity.GetRequestTenant(context);
        var profile = await accessProfileRepo.GetProfileAsync(tenantId, id, ct).ConfigureAwait(false);
        if (profile == null)
        {
            return Results.NotFound(new { error = $"Profile '{id}' not found." });
        }

        await accessProfileRepo.DeleteProfileAsync(tenantId, id, ct).ConfigureAwait(false);

        var callerSid = context.User.GetUserSid()?.Value ?? context.User.Identity?.Name ?? "UNKNOWN";
        var auditEvent = new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = "CONSENT_REVOKED",
            ActorSid = context.User.GetUserSid() ?? new Sid(callerSid),
            TargetTable = string.Join(",", profile.TargetTables),
            Decision = "DENY",
            TraceId = context.TraceIdentifier,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                profileId = id,
                profile.Name,
                profile.MaskingMode,
                profile.TargetTables,
                profile.AssignedSubjects,
                revokedBy = callerSid
            })
        };

        var auditRepo = context.RequestServices.GetService<IAuditLogRepository>();
        if (auditRepo != null)
        {
            await auditRepo.RecordAuditEventAsync(auditEvent, ct).ConfigureAwait(false);
        }

        // Cluster-weite Cache-Invalidierung (AR-01)
        var accessProfileCache = context.RequestServices.GetService<Autheris.Application.Policy.Interfaces.IAccessProfileCache>();
        if (accessProfileCache != null)
        {
            try
            {
                await accessProfileCache.InvalidateTenantAsync(tenantId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (auditRepo != null)
                {
                    var failAudit = new AuditLogEntry
                    {
                        TenantId = tenantId,
                        EventType = "PROFILE_INVALIDATION_FAILED",
                        ActorSid = context.User.GetUserSid() ?? new Sid(callerSid),
                        Decision = "DENY",
                        TraceId = context.TraceIdentifier,
                        DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            reason = ex.Message,
                            tenant = tenantId.Value,
                            profileId = id.ToString()
                        })
                    };
                    await auditRepo.RecordAuditEventAsync(failAudit, ct).ConfigureAwait(false);
                }
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }
        else
        {
            var clusterState = context.RequestServices.GetService<IDistributedClusterStateProvider>();
            if (clusterState != null)
            {
                var bumped = await clusterState.IncrementAsync($"profile_epoch:{tenantId.Value}", 1, TimeSpan.Zero, ct).ConfigureAwait(false);
                if (!bumped.HasValue)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            }
        }

        return Results.Ok(new { message = $"Profile '{id}' successfully revoked.", auditEventId = auditEvent.Id });
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

public sealed class BulkConsentRequest
{
    public string? Subject { get; set; }
    public string? SubjectType { get; set; }
    public string? MaskingMode { get; set; }
    public List<string>? Tables { get; set; }
    public string? RowFilter { get; set; }
    public int ValidDays { get; set; }
    public string? Justification { get; set; }
}
