namespace Autheris.Api.Endpoints;

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

public static partial class GovernanceEndpoints
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

    private static TenantId ResolveRequestTenant(HttpContext context) =>
        context.Items.TryGetValue(TenantResolutionMiddleware.TenantIdItemKey, out var itemTenant) && itemTenant is TenantId resolvedTenant
            ? resolvedTenant
            : TenantId.LegacySingleTenant;

    /// <summary>
    /// WP-SC-10 / SEC-SC-16: keyset-paged audit listing. Tenant administrators and auditors read their tenant; a data owner reads only the
    /// audit of one table they own. A foreign tenant is honoured for ClusterAdmin only. A malformed cursor is a 400, never a silent restart.
    /// </summary>
    internal static async Task<IResult> ListAuditPageAsync(
        HttpContext context,
        IAuditLogRepository auditRepository,
        string? cursor,
        int? pageSize,
        string? targetTable,
        string? actor,
        DateTimeOffset? since,
        string? tenant)
    {
        if (!await IsAuthorizedForSimulationAsync(context, targetTable).ConfigureAwait(false))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var effectiveTenant = ResolveRequestTenant(context);
        if (!string.IsNullOrWhiteSpace(tenant) && !string.Equals(tenant, effectiveTenant.Value, StringComparison.Ordinal))
        {
            if (!GatewayPolicies.HasRole(context.User, GatewayRole.ClusterAdmin) || !TenantId.TryParse(tenant, out var requested))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            effectiveTenant = requested;
        }

        long? before = null;
        if (cursor != null)
        {
            if (!PageCursor.TryDecodeLong(cursor, out var decoded))
            {
                return Results.BadRequest(new { error = "INVALID_CURSOR", message = "The cursor is malformed." });
            }

            before = decoded;
        }

        var page = await auditRepository.QueryAuditLogPageAsync(
            new AuditLogPageQuery(
                effectiveTenant,
                string.IsNullOrWhiteSpace(targetTable) ? null : targetTable,
                string.IsNullOrWhiteSpace(actor) ? (Sid?)null : new Sid(actor),
                since,
                before,
                pageSize ?? AuditLogPageQuery.DefaultPageSize),
            context.RequestAborted).ConfigureAwait(false);

        return Results.Ok(new
        {
            items = page.Items.Select(e => new
            {
                sequence = e.ChainSequence,
                occurredAt = e.OccurredAt,
                eventType = e.EventType,
                decision = e.Decision,
                actorSid = e.ActorSid.Value,
                targetTable = e.TargetTable,
                targetColumn = e.TargetColumn,
                traceId = e.TraceId,
                tenantId = e.TenantId.Value
            }),
            nextCursor = page.NextBeforeSequence is { } next ? PageCursor.Encode(next) : null,
            pageSize = Math.Clamp(pageSize ?? AuditLogPageQuery.DefaultPageSize, 1, AuditLogPageQuery.MaxPageSize)
        });
    }

    /// <summary>
    /// WP-SC-10: keyset-paged consent / grant listing (also the <c>clamped=true</c> report of legacy individual grants, WP-SC-16 rule 5).
    /// Same authorization as the audit listing: tenant administrators and auditors for the tenant, a data owner for one owned table.
    /// </summary>
    internal static async Task<IResult> ListGrantsPageAsync(
        HttpContext context,
        IConsentRepository consentRepository,
        TimeProvider? timeProvider,
        string? cursor,
        int? pageSize,
        string? table,
        string? granteeType,
        string? grantee,
        bool? includeInactive,
        bool? clamped)
    {
        if (!await IsAuthorizedForSimulationAsync(context, table).ConfigureAwait(false))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        TableIdentifier? tableFilter = null;
        if (!string.IsNullOrWhiteSpace(table))
        {
            if (!TableIdentifier.TryParse(table, out var parsedTable))
            {
                return Results.BadRequest(new { error = "INVALID_TABLE", message = "The table filter is malformed." });
            }

            tableFilter = parsedTable;
        }

        GranteeType? typeFilter = null;
        if (!string.IsNullOrWhiteSpace(granteeType))
        {
            if (!Enum.TryParse<GranteeType>(granteeType, ignoreCase: true, out var parsedType) || !Enum.IsDefined(parsedType))
            {
                return Results.BadRequest(new { error = "INVALID_GRANTEE_TYPE", message = "Supported values: User, Group, Role, ServicePrincipal." });
            }

            typeFilter = parsedType;
        }

        Guid? after = null;
        if (cursor != null)
        {
            if (!PageCursor.TryDecode(cursor, out var raw) || !Guid.TryParse(raw, out var afterId))
            {
                return Results.BadRequest(new { error = "INVALID_CURSOR", message = "The cursor is malformed." });
            }

            after = afterId;
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var page = await ConsentPageReader.ReadAsync(
            consentRepository,
            new ConsentPageQuery(
                ResolveRequestTenant(context),
                tableFilter,
                typeFilter,
                string.IsNullOrWhiteSpace(grantee) ? null : grantee,
                includeInactive == true,
                after,
                pageSize ?? ConsentPageQuery.DefaultPageSize),
            clamped == true,
            now,
            context.RequestAborted).ConfigureAwait(false);

        var descriptionMax = (context.RequestServices.GetService(typeof(Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GovernanceTextOptions>))
            as Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GovernanceTextOptions>)?.Value.ConsentDescriptionMaxLength
            ?? new Autheris.Domain.Options.GovernanceTextOptions().ConsentDescriptionMaxLength;
        return Results.Ok(new
        {
            items = page.Items.Select(c => new
            {
                id = c.Id,
                table = c.TableIdentifier.ToString(),
                description = Autheris.Domain.Security.ConsentDescriptionText.ForOutput(c.Description, descriptionMax),
                granteeType = c.GranteeType.ToString(),
                grantee = c.GranteeType == Autheris.Domain.Model.GranteeType.Role ? c.RoleName : c.GranteeSid?.Value,
                effect = c.Effect.ToString(),
                validFrom = c.ValidFrom,
                validTo = c.ValidTo,
                effectiveValidTo = c.EffectiveValidTo,
                clamped = ConsentPageReader.IsClamped(c),
                isActive = c.IsActive(now),
                renewalCount = c.RenewalCount,
                message = ConsentPageReader.IsClamped(c)
                    ? $"individual grant without expiry/over limit, treated as expiring at {c.EffectiveValidTo:O}"
                    : null
            }),
            nextCursor = page.NextAfterId is { } next ? PageCursor.Encode(next.ToString("D")) : null,
            pageSize = Math.Clamp(pageSize ?? ConsentPageQuery.DefaultPageSize, 1, ConsentPageQuery.MaxPageSize)
        });
    }

    private static void MapAuditAndComplianceEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/governance/audit", ListAuditPageAsync)
            .RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.AuditRead);
        app.MapGet("/api/v1/governance/grants", ListGrantsPageAsync)
            .RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.AuditRead);

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
                         ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
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

            var auditRepo = context.RequestServices.GetRequiredService<Autheris.Application.Audit.IAuditWriter>();
            await auditRepo.RecordRequiredAsync(new AuditLogEntry
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
            }, CancellationToken.None).ConfigureAwait(false);

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
          .WithAudit(AuditLevel.Full, AuditEventTypes.AuditExport);

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
    }
}
