namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// Virtual filters (docs/plans/2026-10-08-umsetzungsplan-virtuelle-filter.md, phase 2): administration API.
/// FilterAdmin maintains filters and access profiles, FilterSync applies the state of the file repository (Talos),
/// GovernanceAdmin and SecurityAuditor read. Callers stay within their tenant; only ClusterAdmin acts across tenants.
/// </summary>
public static partial class VirtualFilterEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IEndpointRouteBuilder MapVirtualFilterEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/api/v1/governance")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        group.MapGet("/virtual-filters", (HttpContext context, VirtualFilterAdministrationService service) => ListAsync(context, service))
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        group.MapPut("/virtual-filters/{name}", (string name, HttpContext context, VirtualFilterAdministrationService service) => PutFilterAsync(name, context, service));
        group.MapPost("/virtual-filters/{name}/approve", (string name, HttpContext context, VirtualFilterAdministrationService service) => ApproveFilterAsync(name, context, service))
            .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);
        group.MapDelete("/virtual-filters/{name}", (string name, HttpContext context, VirtualFilterAdministrationService service) => DeleteFilterAsync(name, context, service))
            .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentRevoked);
        group.MapPut("/access-profiles/{name}", (string name, HttpContext context, VirtualFilterAdministrationService service) => PutProfileAsync(name, context, service));
        group.MapPost("/access-profiles/{name}/approve", (string name, HttpContext context, VirtualFilterAdministrationService service) => ApproveProfileAsync(name, context, service))
            .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);
        group.MapDelete("/access-profiles/{name}", (string name, HttpContext context, VirtualFilterAdministrationService service) => DeleteProfileAsync(name, context, service))
            .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentRevoked);
        group.MapPost("/virtual-filters/sync/plan", (HttpContext context, VirtualFilterAdministrationService service) => PlanSyncAsync(context, service));
        group.MapPost("/virtual-filters/sync/apply", (HttpContext context, VirtualFilterAdministrationService service) => ApplySyncAsync(context, service));
        group.MapGet("/config-sync/status", (HttpContext context, VirtualFilterAdministrationService service, IOptions<GatewayOptions>? options) =>
            ConfigSyncStatusAsync(context, service, options))
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        group.MapGet("/virtual-filters/sync/status", (HttpContext context, VirtualFilterAdministrationService service, IOptions<GatewayOptions>? options) =>
            ConfigSyncStatusAsync(context, service, options))
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        group.MapGet("/effective-filters", (HttpContext context, IEffectiveFilterInspector inspector) =>
            EffectiveFiltersAsync(context, inspector))
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // GitOps Webhook Triggers (GitHub push events)
        app.MapPost("/api/webhooks/config-sync", (
            HttpContext context,
            VirtualFilterAdministrationService service,
            IOptions<GatewayOptions> options) => HandleConfigSyncWebhookAsync(context, service, options))
            .AllowAnonymous()
            .WithRequestBodyLimit(2 * 1024 * 1024)
            .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        app.MapPost("/api/v1/governance/virtual-filters/sync/webhook", (
            HttpContext context,
            VirtualFilterAdministrationService service,
            IOptions<GatewayOptions> options) => HandleConfigSyncWebhookAsync(context, service, options))
            .AllowAnonymous()
            .WithRequestBodyLimit(2 * 1024 * 1024)
            .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        return app;
    }

    /// <summary>
    /// Phase 6: for <c>user</c> (SID, optional <c>groups</c> and <c>roles</c> as comma separated lists) and <c>table</c>
    /// (domain.schema.table) which bindings apply, which columns are missing, what supersedes what, the decision and the
    /// SQL in the table's dialect. Without <c>table</c>: one line per catalog object (<c>page</c>, <c>pageSize</c>).
    /// </summary>
    internal static async Task<IResult> EffectiveFiltersAsync(HttpContext context, IEffectiveFilterInspector inspector)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        var q = context.Request.Query;
        var inspection = await inspector.InspectAsync(
            security,
            new EffectiveFilterQuery(q["user"], q["groups"], q["roles"], q["tenant"], q["table"], q["page"], q["pageSize"]),
            context.RequestAborted).ConfigureAwait(false);

        switch (inspection.Kind)
        {
            case EffectiveFilterInspectionKind.Forbidden:
                return Forbidden();
            case EffectiveFilterInspectionKind.BadRequest:
                return Results.Json(new { error = inspection.Error }, statusCode: StatusCodes.Status400BadRequest);
            case EffectiveFilterInspectionKind.NotFound:
                return Results.Json(new { error = inspection.Error }, statusCode: StatusCodes.Status404NotFound);
            case EffectiveFilterInspectionKind.SingleTable:
                return Results.Ok(ExplanationView(inspection.User!, inspection.Table!, inspection.Explanation!));
            case EffectiveFilterInspectionKind.Page:
                var objects = inspection.Items.Select(i => (object)new
                {
                    table = i.Table,
                    decision = i.Decision,
                    applied_filters = i.AppliedFilters,
                    reason = i.Reason
                }).ToList();
                return Results.Ok(new { user = inspection.User, page = inspection.PageNo, page_size = inspection.PageSize, total = inspection.Total, objects });
            default:
                throw new UnreachableException();
        }
    }

    private static object ExplanationView(string user, TableMetadata table, MandatoryFilterExplanation explanation) => new
    {
        user,
        table = table.Identifier.ToString(),
        dialect = table.Dialect.ToString(),
        generation = explanation.Generation,
        decision = explanation.Decision,
        reason = explanation.Outcome.DenyReason,
        sql = explanation.Outcome.PredicateSql,
        applied_filters = explanation.Outcome.AppliedFilters,
        profiles = explanation.Profiles.Select(p => new
        {
            profile = p.Profile,
            scope = p.Scope,
            in_scope = p.InScope,
            uncovered = p.Uncovered is { } uncovered ? VirtualFilterBindingValidator.FormatEnum(uncovered) : null,
            bindings = p.Bindings.Select(b => new
            {
                filter = b.Filter,
                pattern = b.Pattern,
                applies = b.Applies,
                reason = b.Reason,
                missing_columns = b.MissingColumns,
                superseded_by = b.SupersededBy
            })
        })
    };

    private static bool CanRead(SecurityPrincipalContext security) =>
        security.HasRole(GatewayRole.FilterAdmin) || security.HasRole(GatewayRole.FilterSync) ||
        security.HasRole(GatewayRole.GovernanceAdmin) || security.HasRole(GatewayRole.SecurityAuditor);

    private static async Task<IResult> ExecuteAsync(
        HttpContext context,
        GatewayRole requiredRole,
        Func<SecurityPrincipalContext, VirtualFilterActor, Task<IResult>> action)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        if (!security.HasRole(requiredRole))
        {
            var audit = context.RequestServices.GetRequiredService<Autheris.Application.Audit.IAuditWriter>();
            await audit.RecordRequiredAsync(new AuditLogEntry
            {
                EventType = "VIRTUAL_FILTER_ADMIN_DENIED",
                TargetTable = context.Request.Path.Value ?? "virtual_filter",
                ActorSid = security.UserSid,
                TenantId = security.TenantId,
                Decision = "DENY",
                DetailsJson = JsonSerializer.Serialize(new { error = "Forbidden: missing required role", requiredRole = requiredRole.ToString() }),
                OccurredAt = DateTimeOffset.UtcNow
            }, context.RequestAborted).ConfigureAwait(false);
            return Forbidden();
        }

        var actor = new VirtualFilterActor(security.UserSid, IsSync: false, context.User?.GetUserIdentifiers());
        return await MapErrorsAsync(() => action(security, actor), context).ConfigureAwait(false);
    }

    internal static async Task<IResult> MapErrorsAsync(Func<Task<IResult>> action, HttpContext? context = null)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ManagedResourceLockedException ex)
        {
            return Problem(context, ex, StatusCodes.Status409Conflict, ApiErrorCodes.ResourceLocked, "The virtual filter resource is locked by another source.");
        }
        catch (VirtualFilterConflictException ex)
        {
            return Problem(context, ex, StatusCodes.Status409Conflict, ApiErrorCodes.VirtualFilterConflict, "The virtual filter change conflicts with existing state.");
        }
        catch (KeyNotFoundException ex)
        {
            return Problem(context, ex, StatusCodes.Status404NotFound, ApiErrorCodes.ResourceNotFound, "The requested virtual filter resource was not found.");
        }
        catch (InvalidOperationException ex)
        {
            return Problem(context, ex, StatusCodes.Status400BadRequest, ApiErrorCodes.VirtualFilterInvalid, "The requested virtual filter operation is invalid.");
        }
        catch (VirtualFilterBindingException ex)
        {
            // The code is echoed only when it is one of the governed codes; the detail is fixed per code (never the message).
            return VirtualFilterErrorCodes.IsGoverned(ex.Code)
                ? Problem(context, ex, StatusCodes.Status400BadRequest, ex.Code, "The virtual filter binding is invalid.")
                : Problem(context, ex, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest, "The request is invalid.");
        }
        catch (JsonException ex)
        {
            return Problem(context, ex, StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidJson, "The request body is not valid JSON.");
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return Problem(context, ex, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest, "The request is invalid.");
        }
    }

    private static IResult Problem(HttpContext? context, Exception ex, int status, string code, string detail)
    {
        ApiProblems.LogFailure(context, ex, code);
        return ApiProblems.Problem(status, code, detail, context);
    }

    /// <summary>The caller's tenant, or the requested one for ClusterAdmin (decision 3); null when forbidden.</summary>
    private static TenantId? ResolveTenant(SecurityPrincipalContext security, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || string.Equals(requested, security.TenantId.Value, StringComparison.Ordinal))
        {
            return security.TenantId;
        }

        return security.IsClusterAdmin ? new TenantId(requested) : (TenantId?)null;
    }

    private static async Task<T> ReadAsync<T>(HttpContext context)
    {
        return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, JsonOptions, context.RequestAborted).ConfigureAwait(false)
               ?? throw new ArgumentException("The request body is empty.");
    }

    private static IResult Forbidden() => Results.StatusCode(StatusCodes.Status403Forbidden);
}
