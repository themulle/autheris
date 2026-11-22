namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints.Support;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Security;
using Autheris.Application.Webhooks;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Admin endpoints for consent workflow routes, external workflow systems, and controlled open-request rerouting.
/// </summary>
public static partial class ConsentWorkflowAdminEndpoints
{
    public static IEndpointRouteBuilder MapConsentWorkflowAdminEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/admin/consent")
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
            .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentWorkflowConfigChanged);

        group.MapPut("/routes/{scopeKind}/{scopeRef}", UpsertRouteAsync);
        group.MapGet("/routes/{scopeKind}/{scopeRef}", GetRouteAsync)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        group.MapDelete("/routes/{scopeKind}/{scopeRef}", DeleteRouteAsync);

        group.MapPut("/external-systems/{id:guid}", UpsertExternalSystemAsync);
        group.MapGet("/external-systems/{id:guid}", GetExternalSystemAsync)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        group.MapPost("/external-systems/{id:guid}:disable", DisableExternalSystemAsync);

        group.MapPost("/requests:reroute", RerouteRequestsAsync);

        app.MapGet("/api/v1/admin/consent/grant-records/{grantId:guid}/export", ExportGrantRecordAsync)
            .RequireAuthorization(GatewayPolicies.AuditRead)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.AuditExport);

        app.MapPost("/api/v1/admin/consent/grant-records:verify", VerifyGrantRecordsAsync)
            .RequireAuthorization(GatewayPolicies.AuditRead)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.AuditRead);

        return app;
    }

    internal static async Task<IResult> UpsertRouteAsync(
        string scopeKind,
        string scopeRef,
        UpsertWorkflowRouteRequest? body,
        HttpContext context,
        IConsentWorkflowAdminRepository adminRepo,
        IConsentWorkflowConfigRepository configRepo,
        TimeProvider? timeProvider,
        CancellationToken ct)
    {
        if (body is null)
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Request body is required.");
        }

        if (!TryParseScopeKind(scopeKind, out var parsedScope))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Unsupported route scopeKind.");
        }

        if (!TryParseWorkflowMode(body.Mode, out var parsedMode))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Unsupported workflow mode.");
        }

        if (body.MaxDurationDays <= 0 || body.RequestTimeoutDays <= 0 || body.ExpiryNoticeLeadHours <= 0)
        {
            return SafeErrorResults.Problem(
                StatusCodes.Status422UnprocessableEntity,
                ApiErrorCodes.BadRequest,
                "Route duration, timeout, and notice lead hours must be positive.",
                context);
        }

        var (tenantId, actorSid) = ResolveTenantAndActor(context);
        var normalizedScopeRef = parsedScope == WorkflowRouteScopeKind.Tenant
            ? "*"
            : scopeRef.Trim();
        if (string.IsNullOrWhiteSpace(normalizedScopeRef))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Route scopeRef is required.");
        }

        if (parsedMode == ConsentWorkflowMode.External)
        {
            if (!body.ExternalSystemId.HasValue)
            {
                return SafeErrorResults.Problem(
                    StatusCodes.Status422UnprocessableEntity,
                    ApiErrorCodes.BadRequest,
                    "External workflow routes require externalSystemId.",
                    context);
            }

            var system = await configRepo.GetExternalWorkflowSystemAsync(
                tenantId, body.ExternalSystemId.Value, ct).ConfigureAwait(false);
            if (system is null || !system.Enabled)
            {
                return SafeErrorResults.Problem(
                    StatusCodes.Status422UnprocessableEntity,
                    ApiErrorCodes.BadRequest,
                    "Referenced external workflow system was not found or is disabled.",
                    context);
            }
        }

        if (parsedScope is WorkflowRouteScopeKind.DataOwner or WorkflowRouteScopeKind.DataSource)
        {
            var tenantRoute = await adminRepo.GetWorkflowRouteAsync(
                tenantId, WorkflowRouteScopeKind.Tenant, "*", ct).ConfigureAwait(false);
            if (tenantRoute is not null && body.MaxDurationDays > tenantRoute.MaxDurationDays)
            {
                return SafeErrorResults.Problem(
                    StatusCodes.Status422UnprocessableEntity,
                    ConsentDecisionErrorCodes.ValidityExceedsMaximum,
                    "Owner or data-source route maxDurationDays cannot exceed the tenant route maximum.",
                    context);
            }
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var route = new ConsentWorkflowRoute(
            tenantId.Value,
            parsedScope,
            normalizedScopeRef,
            parsedMode,
            parsedMode == ConsentWorkflowMode.External ? body.ExternalSystemId : null,
            body.MaxDurationDays,
            body.RequestTimeoutDays,
            body.ExpiryNoticeLeadHours,
            body.RequireResolvableApprover ?? true,
            actorSid,
            now);

        var saved = await adminRepo.UpsertWorkflowRouteAsync(route, ct).ConfigureAwait(false);
        return Results.Ok(ToRouteResponse(saved));
    }

    internal static async Task<IResult> GetRouteAsync(
        string scopeKind,
        string scopeRef,
        HttpContext context,
        IConsentWorkflowAdminRepository adminRepo,
        CancellationToken ct)
    {
        if (!TryParseScopeKind(scopeKind, out var parsedScope))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Unsupported route scopeKind.");
        }

        var (tenantId, _) = ResolveTenantAndActor(context);
        var normalizedScopeRef = parsedScope == WorkflowRouteScopeKind.Tenant ? "*" : scopeRef.Trim();
        var route = await adminRepo.GetWorkflowRouteAsync(tenantId, parsedScope, normalizedScopeRef, ct).ConfigureAwait(false);
        return route is null
            ? ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "Workflow route was not found.")
            : Results.Ok(ToRouteResponse(route));
    }

    internal static async Task<IResult> DeleteRouteAsync(
        string scopeKind,
        string scopeRef,
        HttpContext context,
        IConsentWorkflowAdminRepository adminRepo,
        CancellationToken ct)
    {
        if (!TryParseScopeKind(scopeKind, out var parsedScope))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Unsupported route scopeKind.");
        }

        var (tenantId, _) = ResolveTenantAndActor(context);
        var normalizedScopeRef = parsedScope == WorkflowRouteScopeKind.Tenant ? "*" : scopeRef.Trim();
        var deleted = await adminRepo.DeleteWorkflowRouteAsync(tenantId, parsedScope, normalizedScopeRef, ct).ConfigureAwait(false);
        return deleted
            ? Results.NoContent()
            : ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "Workflow route was not found.");
    }

    internal static async Task<IResult> UpsertExternalSystemAsync(
        Guid id,
        UpsertExternalSystemRequest? body,
        HttpContext context,
        IConsentWorkflowAdminRepository adminRepo,
        IConsentWorkflowConfigRepository configRepo,
        IHostEnvironment environment,
        TimeProvider? timeProvider,
        CancellationToken ct)
    {
        if (body is null
            || string.IsNullOrWhiteSpace(body.Name)
            || string.IsNullOrWhiteSpace(body.ClientId)
            || string.IsNullOrWhiteSpace(body.WebhookUrl))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "name, clientId, and webhookUrl are required.");
        }

        if (!Uri.TryCreate(body.WebhookUrl, UriKind.Absolute, out var parsedUrl))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "webhookUrl must be a valid absolute URL.");
        }

        try
        {
            EgressUrlPolicy.ValidateStatic(parsedUrl, environment.IsDevelopment());
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or ArgumentException)
        {
            return SafeErrorResults.Problem(
                StatusCodes.Status422UnprocessableEntity,
                ApiErrorCodes.BadRequest,
                "webhookUrl violates outbound egress policy.",
                context);
        }

        var (tenantId, actorSid) = ResolveTenantAndActor(context);
        var existing = await configRepo.GetExternalWorkflowSystemAsync(tenantId, id, ct).ConfigureAwait(false);

        string protectedSecret;
        if (!string.IsNullOrEmpty(body.WebhookSecret))
        {
            var protector = context.RequestServices.GetService<IWebhookSecretProtector>();
            if (protector is null || !protector.IsAvailable)
            {
                return SafeErrorResults.Problem(
                    StatusCodes.Status503ServiceUnavailable,
                    ApiErrorCodes.ServiceUnavailable,
                    "The webhook secret protector is not available.",
                    context);
            }

            protectedSecret = protector.Protect(tenantId.Value, id.ToString("D"), body.WebhookSecret);
        }
        else if (existing is not null && !string.IsNullOrEmpty(existing.WebhookSecretProtected))
        {
            protectedSecret = existing.WebhookSecretProtected;
        }
        else
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "webhookSecret is required when creating a new external workflow system.");
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var system = new ExternalWorkflowSystem(
            id,
            tenantId.Value,
            body.Name.Trim(),
            body.ClientId.Trim(),
            body.WebhookUrl.Trim(),
            protectedSecret,
            body.Enabled ?? true,
            existing?.CreatedBy ?? actorSid,
            existing?.CreatedAt ?? now,
            (body.Enabled ?? true) ? null : existing?.DisabledAt ?? now);

        var saved = await adminRepo.UpsertExternalWorkflowSystemAsync(system, ct).ConfigureAwait(false);
        return Results.Ok(ToExternalSystemResponse(saved));
    }

    internal static async Task<IResult> GetExternalSystemAsync(
        Guid id,
        HttpContext context,
        IConsentWorkflowConfigRepository configRepo,
        CancellationToken ct)
    {
        var (tenantId, _) = ResolveTenantAndActor(context);
        var system = await configRepo.GetExternalWorkflowSystemAsync(tenantId, id, ct).ConfigureAwait(false);
        return system is null
            ? ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "External workflow system was not found.")
            : Results.Ok(ToExternalSystemResponse(system));
    }

    internal static async Task<IResult> DisableExternalSystemAsync(
        Guid id,
        HttpContext context,
        IConsentWorkflowAdminRepository adminRepo,
        TimeProvider? timeProvider,
        CancellationToken ct)
    {
        var (tenantId, actorSid) = ResolveTenantAndActor(context);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var (system, rerouteResult) = await adminRepo.DisableExternalWorkflowSystemAsync(
            tenantId, id, actorSid, now, ct).ConfigureAwait(false);

        if (system is null)
        {
            return ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "External workflow system was not found.");
        }

        return Results.Ok(new
        {
            system = ToExternalSystemResponse(system),
            reroutedCount = rerouteResult.ReroutedCount,
            reroutedRequestIds = rerouteResult.ReroutedRequestIds
        });
    }

    internal static async Task<IResult> RerouteRequestsAsync(
        RerouteRequestsBody? body,
        HttpContext context,
        IConsentWorkflowAdminRepository adminRepo,
        IConsentWorkflowConfigRepository configRepo,
        TimeProvider? timeProvider,
        CancellationToken ct)
    {
        if (body is null || !TryParseWorkflowMode(body.TargetMode, out var targetMode))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "A valid targetMode is required.");
        }

        ConsentWorkflowMode? fromMode = null;
        if (!string.IsNullOrWhiteSpace(body.FromMode))
        {
            if (!TryParseWorkflowMode(body.FromMode, out var parsedFrom))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Invalid fromMode.");
            }

            fromMode = parsedFrom;
        }

        var (tenantId, actorSid) = ResolveTenantAndActor(context);
        if (targetMode == ConsentWorkflowMode.External)
        {
            if (!body.TargetExternalSystemId.HasValue)
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "targetExternalSystemId is required for external targetMode.");
            }

            var targetSystem = await configRepo.GetExternalWorkflowSystemAsync(
                tenantId, body.TargetExternalSystemId.Value, ct).ConfigureAwait(false);
            if (targetSystem is null || !targetSystem.Enabled)
            {
                return SafeErrorResults.Problem(
                    StatusCodes.Status422UnprocessableEntity,
                    ApiErrorCodes.BadRequest,
                    "Target external workflow system was not found or is disabled.",
                    context);
            }
        }

        try
        {
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            var result = await adminRepo.RerouteOpenRequestsAsync(
                tenantId,
                body.FromExternalSystemId,
                fromMode,
                targetMode,
                body.TargetExternalSystemId,
                actorSid,
                now,
                body.RequestIds,
                ct).ConfigureAwait(false);

            return Results.Ok(new
            {
                reroutedCount = result.ReroutedCount,
                rejectedCount = result.RejectedCount,
                reroutedRequestIds = result.ReroutedRequestIds
            });
        }
        catch (ConsentDecisionException ex)
        {
            return SafeErrorResults.Problem(
                ex.StatusCode,
                ex.ErrorCode,
                "One or more targeted requests cannot be rerouted.",
                context);
        }
    }

    private static (TenantId TenantId, string ActorSid) ResolveTenantAndActor(HttpContext context)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        var tenantId = security?.TenantId ?? EndpointSecurity.GetRequestTenant(context);
        var actorSid = security?.UserSid.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst("sub")?.Value
            ?? "SYSTEM";
        return (tenantId, actorSid);
    }

    private static bool TryParseScopeKind(string? value, out WorkflowRouteScopeKind scopeKind)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "TENANT":
                scopeKind = WorkflowRouteScopeKind.Tenant;
                return true;
            case "DATA_SOURCE":
            case "DATASOURCE":
                scopeKind = WorkflowRouteScopeKind.DataSource;
                return true;
            case "DATA_OWNER":
            case "DATAOWNER":
                scopeKind = WorkflowRouteScopeKind.DataOwner;
                return true;
            default:
                scopeKind = default;
                return false;
        }
    }

    private static bool TryParseWorkflowMode(string? value, out ConsentWorkflowMode mode)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "INTERNAL":
                mode = ConsentWorkflowMode.Internal;
                return true;
            case "EXTERNAL":
                mode = ConsentWorkflowMode.External;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    private static object ToRouteResponse(ConsentWorkflowRoute route) => new
    {
        tenantId = route.TenantId,
        scopeKind = route.ScopeKind.ToSqlString(),
        scopeRef = route.ScopeRef,
        mode = route.Mode.ToSqlString(),
        externalSystemId = route.ExternalSystemId,
        maxDurationDays = route.MaxDurationDays,
        requestTimeoutDays = route.RequestTimeoutDays,
        expiryNoticeLeadHours = route.ExpiryNoticeLeadHours,
        requireResolvableApprover = route.RequireResolvableApprover,
        updatedBy = route.UpdatedBy,
        updatedAt = route.UpdatedAt
    };

    private static object ToExternalSystemResponse(ExternalWorkflowSystem system) => new
    {
        id = system.Id,
        tenantId = system.TenantId,
        name = system.Name,
        clientId = system.ClientId,
        webhookUrl = system.WebhookUrl,
        enabled = system.Enabled,
        createdBy = system.CreatedBy,
        createdAt = system.CreatedAt,
        disabledAt = system.DisabledAt
    };

    public sealed record UpsertWorkflowRouteRequest(
        string Mode,
        Guid? ExternalSystemId,
        int MaxDurationDays,
        int RequestTimeoutDays = 30,
        int ExpiryNoticeLeadHours = 72,
        bool? RequireResolvableApprover = true);

    public sealed record UpsertExternalSystemRequest(
        string Name,
        string ClientId,
        string WebhookUrl,
        string? WebhookSecret,
        bool? Enabled = true);

    public sealed record RerouteRequestsBody(
        string TargetMode,
        Guid? TargetExternalSystemId = null,
        Guid? FromExternalSystemId = null,
        string? FromMode = null,
        IReadOnlyList<Guid>? RequestIds = null);
}
