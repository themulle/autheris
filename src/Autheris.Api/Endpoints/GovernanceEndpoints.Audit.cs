namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
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

    // DT-A-3: no sentinel; an unresolved request tenant throws TENANT_UNRESOLVED (mapped to 403) at the tenant read.
    private static TenantId ResolveRequestTenant(HttpContext context) => EndpointSecurity.GetRequestTenant(context);

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
            // DT-A-6: the platform bucket (_platform) is readable by ClusterAdmin only; every other reserved value stays rejected.
            if (!GatewayPolicies.HasRole(context.User, GatewayRole.ClusterAdmin) || !StoredTenant.IsValidAudit(tenant))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            effectiveTenant = StoredTenant.ParseAudit(tenant);
        }

        long? before = null;
        if (cursor != null)
        {
            if (!PageCursor.TryDecodeLong(cursor, out var decoded))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.InvalidCursor, "The cursor is malformed.", context);
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
                return ApiProblems.BadRequest(ApiErrorCodes.InvalidTable, "The table filter is malformed.", context);
            }

            tableFilter = parsedTable;
        }

        GranteeType? typeFilter = null;
        if (!string.IsNullOrWhiteSpace(granteeType))
        {
            if (!Enum.TryParse<GranteeType>(granteeType, ignoreCase: true, out var parsedType) || !Enum.IsDefined(parsedType))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.InvalidGranteeType, "Supported values: User, Group, Role, ServicePrincipal.", context);
            }

            typeFilter = parsedType;
        }

        Guid? after = null;
        if (cursor != null)
        {
            if (!PageCursor.TryDecode(cursor, out var raw) || !Guid.TryParse(raw, out var afterId))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.InvalidCursor, "The cursor is malformed.", context);
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

        await Autheris.Application.Governance.ConsentDescriptionLoader.LoadAsync(consentRepository, page.Items, context.RequestAborted).ConfigureAwait(false);
        var descriptionMax = context.RequestServices.GetService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GovernanceTextOptions>>()?.Value.ConsentDescriptionMaxLength
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
        app.MapPost("/api/governance/policy-simulation/replay", (
            PolicySimulationRequest? request,
            IPolicySimulationService simulationService,
            HttpContext context) => ReplayPolicySimulationAsync(request, simulationService, context)).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.AuditRead);

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

        app.MapPost("/api/governance/sunsetting/rules", (
            FieldSunsettingRule? rule,
            ISchemaSunsettingService sunsettingService,
            HttpContext context) => RegisterSunsettingRuleAsync(rule, sunsettingService, context))
            .RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        app.MapPost("/api/governance/sunsetting/evaluate", (
            EvaluateFieldSunsettingRequest? request,
            ISchemaSunsettingService sunsettingService,
            HttpContext context) => EvaluateSunsettingAsync(request, sunsettingService, context)).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.AuthSucceeded);

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
                tenantId = !string.IsNullOrWhiteSpace(reqTenant.Value) ? reqTenant.Value : throw new Autheris.Domain.Exceptions.TenantUnresolvedException();
            }

            var cert = await exporter.GenerateCertificateAsync(tenantId, context.RequestAborted);
            context.Response.Headers["X-Certificate-Id"] = cert.CertificateId;
            context.Response.Headers["X-Integrity-Seal"] = cert.IntegritySealSha256;
            return Results.Ok(cert);
        }).RequireAuthorization()
          .WithAudit(AuditLevel.Full, AuditEventTypes.MetadataExport);

        // GDPR Article 15 PDF Export for Data Protection Officers (DSB)
        app.MapGet("/api/governance/gdpr/export-pdf", (
            string? domain,
            string? schema,
            string? table,
            string? subjectSid,
            int? timeWindowDays,
            ILineageImpactAnalyzerService lineageService,
            IGdprAuditReportExporter pdfExporter,
            HttpContext context,
            CancellationToken ct) => ExportGdprPdfAsync(domain, schema, table, subjectSid, timeWindowDays, lineageService, pdfExporter, context, ct))
          .RequireAuthorization()
          .WithAudit(AuditLevel.Full, AuditEventTypes.AuditExport);

        // OpenLineage Lineage Push Trigger
        app.MapPost("/api/lineage/openlineage/sync", (
            IOpenLineageClient openLineageClient,
            HttpContext context,
            CancellationToken ct) => SyncOpenLineageAsync(openLineageClient, context, ct))
          .RequireAuthorization()
          .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);
    }

    internal static async Task<IResult> ExportGdprPdfAsync(
        string? domain,
        string? schema,
        string? table,
        string? subjectSid,
        int? timeWindowDays,
        ILineageImpactAnalyzerService lineageService,
        IGdprAuditReportExporter pdfExporter,
        HttpContext context,
        CancellationToken ct)
    {
        var principal = context.User;
        var userSid = principal.GetUserSid();
        if (userSid == null || string.IsNullOrWhiteSpace(userSid.Value.Value))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var groupSids = principal.GetGroupSids().ToList();
        var roles = principal.GetUserRoles().ToList();

        var tenantId = EndpointSecurity.GetRequestTenant(context);

        bool isGovAdmin = roles.Contains("GovernanceAdmin", StringComparer.OrdinalIgnoreCase);
        bool isClusterAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        bool isPrivacyAdmin = roles.Contains("PrivacyAdmin", StringComparer.OrdinalIgnoreCase) ||
                              roles.Contains("DataProtectionOfficer", StringComparer.OrdinalIgnoreCase);

        var callerContext = new CallerSecurityContext(
            userSid.Value,
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

        GdprDisclosureReport report;
        try
        {
            report = await lineageService.GetGdprDataDisclosureReportAsync(tableId, effectiveSid, timeWindowDays ?? 365, callerContext, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (Autheris.Application.Lineage.GdprSubjectScopeUnresolvedException)
        {
            // MISC-11: no table scope means no honest report; never a report that implies completeness.
            return ApiProblems.Unprocessable(
                Autheris.Application.Lineage.GdprSubjectScopeUnresolvedException.ErrorCode,
                "The GDPR report scope cannot be resolved: name the table that holds the data subject's data.",
                context);
        }

        var exportResult = pdfExporter.ExportReportToPdf(report);

        // MISC-12: only a keyed seal is published; without a signer the header is omitted.
        if (exportResult.AuditSeal != null)
        {
            context.Response.Headers["X-Audit-Seal"] = exportResult.AuditSeal;
        }

        return Results.File(exportResult.DocumentBytes, exportResult.ContentType, exportResult.FileName);
    }

    internal static async Task<IResult> SyncOpenLineageAsync(
        IOpenLineageClient openLineageClient,
        HttpContext context,
        CancellationToken ct)
    {
        // EP-03: Restrict to GovernanceAdmin only
        if (!GatewayPolicies.HasRole(context.User, GatewayRole.GovernanceAdmin))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        TenantId tenantId;
        try
        {
            tenantId = EndpointSecurity.GetRequestTenant(context);
        }
        catch (System.Security.SecurityException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }
        catch (ArgumentException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var success = await openLineageClient.PushLineageGraphAsync(tenantId, ct);
        if (success)
        {
            return Results.Ok(new { message = "OpenLineage sync completed successfully." });
        }
        return Results.StatusCode(StatusCodes.Status502BadGateway);
    }
}
