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
using Autheris.Application.Audit;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Compliance;
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
        if (GatewayPolicies.HasAnyRequestRole(context, [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin, GatewayRole.SecurityAuditor]))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(targetTable) ||
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
    /// keyset-paged audit listing. Data Governance, Security Auditor and (for a foreign tenant) canonical ClusterAdmin read the tenant; an
    /// owner-group member reads only the entries of objects in the containers whose owner group they belong to. <c>view</c> narrows the listing
    /// (<c>decisions</c>, <c>self-grants</c>, <c>decisions-by-service-principals</c>). A malformed or foreign cursor is a 400, never a silent restart.
    /// </summary>
    internal static async Task<IResult> ListAuditPageAsync(
        HttpContext context,
        IAuditLogRepository auditRepository,
        string? cursor,
        int? pageSize,
        string? targetTable,
        string? actor,
        DateTimeOffset? since,
        string? tenant,
        string? view = null)
    {
        var effectiveTenant = ResolveRequestTenant(context);
        var tenantWide = GatewayPolicies.HasAnyRequestRole(context, [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin, GatewayRole.SecurityAuditor]);
        if (!string.IsNullOrWhiteSpace(tenant) && !string.Equals(tenant, effectiveTenant.Value, StringComparison.Ordinal))
        {
            // DT-A-6: the platform bucket (_platform) is readable by canonical ClusterAdmin only; every other reserved value stays rejected.
            if (!EndpointSecurity.IsCanonicalClusterAdmin(context.User) || !StoredTenant.IsValidAudit(tenant))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            effectiveTenant = StoredTenant.ParseAudit(tenant);
            tenantWide = true;
        }

        SecurityPrincipalContext caller;
        try
        {
            caller = EndpointSecurity.GetSecurityContext(context);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // without the registered service only the tenant-wide path works; an owner scope then fails closed (403)
        var reader = context.RequestServices.GetService<AuditReadService>() ?? new AuditReadService(auditRepository);

        var result = await reader.ReadAsync(
            new AuditReadRequest(caller, effectiveTenant, tenantWide, targetTable, actor, since, view, cursor, pageSize),
            context.RequestAborted).ConfigureAwait(false);

        switch (result.Outcome)
        {
            case AuditReadOutcome.Forbidden:
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            case AuditReadOutcome.InvalidCursor:
                return ApiProblems.BadRequest(ApiErrorCodes.InvalidCursor, "The cursor is malformed.", context);
            case AuditReadOutcome.InvalidView:
                return ApiProblems.BadRequest(
                    ApiErrorCodes.BadRequest,
                    "Supported views: decisions, self-grants, decisions-by-service-principals.",
                    context);
        }

        return Results.Ok(new
        {
            items = result.Items.Select(e => new
            {
                sequence = e.ChainSequence,
                occurredAt = e.OccurredAt,
                eventType = e.EventType,
                decision = e.Decision,
                actorSid = e.ActorSid.Value,
                actorKind = AuditActorKindReader.Read(e),
                targetTable = e.TargetTable,
                targetColumn = e.TargetColumn,
                traceId = e.TraceId,
                tenantId = e.TenantId.Value
            }),
            nextCursor = result.NextCursor,
            pageSize = result.PageSize,
            scopeTruncated = result.ScopeTruncated
        });
    }

    /// <summary>
    /// keyset-paged consent / grant listing (also the<c>clamped=true</c>report of legacy individual grants, 16 rule 5).
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
            if (!GranteeTypeParsing.TryParse(granteeType, out var parsedType))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.InvalidGranteeType, "Supported values: User, Group, ServicePrincipal.", context);
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
                grantee = c.GranteeSid?.Value,
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
            .RequireAuthorization(GatewayPolicies.AuthenticatedCaller).WithAudit(AuditLevel.Summarized, AuditEventTypes.AuditRead);
        app.MapGet("/api/v1/governance/grants", ListGrantsPageAsync)
            .RequireAuthorization(GatewayPolicies.AuditRead).WithAudit(AuditLevel.Summarized, AuditEventTypes.AuditRead);

        // P11: Automated Schema Deprecation & Client-Impact Sunsetting (Smart Sunsetting Engine)
        app.MapGet("/api/governance/sunsetting/rules", async (
            ISchemaSunsettingService sunsettingService,
            HttpContext context) =>
        {
            var isPrivileged = GatewayPolicies.HasAnyRequestRole(context, [GatewayRole.GovernanceAdmin, GatewayRole.SchemaPublisher]);
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var rules = await sunsettingService.GetRulesAsync(context.RequestAborted);
            return Results.Ok(rules);
        }).RequireAuthorization(GatewayPolicies.SunsettingRead).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapPost("/api/governance/sunsetting/rules", (
            FieldSunsettingRule? rule,
            ISchemaSunsettingService sunsettingService,
            HttpContext context) => RegisterSunsettingRuleAsync(rule, sunsettingService, context))
            .RequireAuthorization(GatewayPolicies.ClusterAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        app.MapPost("/api/governance/sunsetting/evaluate", (
            EvaluateFieldSunsettingRequest? request,
            ISchemaSunsettingService sunsettingService,
            HttpContext context) => EvaluateSunsettingAsync(request, sunsettingService, context)).RequireAuthorization(GatewayPolicies.SunsettingEvaluate).WithAudit(AuditLevel.Full, AuditEventTypes.AuthSucceeded);

        // F-AI-12-B: EU AI Act Article 10 Compliance Certificate Endpoint
        app.MapGet("/api/governance/eu-ai-act/article-10-certificate", async (
            IEuAiActAuditExporter exporter,
            HttpContext context) =>
        {
            var isPrivileged = GatewayPolicies.HasAnyRequestRole(context, [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin, GatewayRole.SecurityAuditor]);

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
        }).RequireAuthorization(GatewayPolicies.ComplianceCertificateRead)
          .WithAudit(AuditLevel.Full, AuditEventTypes.MetadataExport);

        // GDPR Article 15 PDF Export for Data Protection Officers (DSB)
        app.MapGet("/api/governance/gdpr/export-pdf", (
            string? domain,
            string? schema,
            string? table,
            string? subjectSid,
            int? timeWindowDays,
            IGdprDisclosureReportService gdprService,
            IGdprAuditReportExporter pdfExporter,
            HttpContext context,
            CancellationToken ct) => ExportGdprPdfAsync(domain, schema, table, subjectSid, timeWindowDays, gdprService, pdfExporter, context, ct))
          .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
          .WithAudit(AuditLevel.Full, AuditEventTypes.AuditExport);
    }

    internal static async Task<IResult> ExportGdprPdfAsync(
        string? domain,
        string? schema,
        string? table,
        string? subjectSid,
        int? timeWindowDays,
        IGdprDisclosureReportService gdprService,
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

        bool isGovAdmin = GatewayPolicies.HasRequestRole(context, GatewayRole.GovernanceAdmin);
        bool isClusterAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);

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

        bool canAccessForeignReports = isGovAdmin || isClusterAdmin;
        var effectiveSid = sid ?? (canAccessForeignReports ? (Sid?)null : callerContext.UserSid);

        if (effectiveSid.HasValue && !effectiveSid.Value.Equals(callerContext.UserSid) && !canAccessForeignReports)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        GdprDisclosureReport report;
        try
        {
            report = await gdprService.GetGdprDataDisclosureReportAsync(tableId, effectiveSid, timeWindowDays ?? 365, callerContext, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (GdprSubjectScopeUnresolvedException)
        {
            // MISC-11: no table scope means no honest report; never a report that implies completeness.
            return ApiProblems.Unprocessable(
                GdprSubjectScopeUnresolvedException.ErrorCode,
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
}
