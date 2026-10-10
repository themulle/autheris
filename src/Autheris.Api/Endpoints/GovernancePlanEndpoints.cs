namespace Autheris.Api.Endpoints;

using System;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// REST endpoints for Two-Phase Access Planning and Confirmation (ADR-03, ADR-05, R-60 to R-64).
/// </summary>
public static class GovernancePlanEndpoints
{
    public static IEndpointRouteBuilder MapGovernancePlanEndpoints(this IEndpointRouteBuilder app)
    {
        // 1. Plan Access: generates preview diffs and warnings without mutating state (R-60)
        app.MapPost("/api/governance/plans", async (
            AdminPlanAccessRequest request,
            HttpContext context,
            IAccessPlanningService planningService,
            CancellationToken ct) =>
        {
            var adminSid = ResolveAdminSid(context);
            if (string.IsNullOrWhiteSpace(adminSid))
            {
                return Results.Unauthorized();
            }

            var result = await planningService.PlanAccessAsync(request, adminSid, ct).ConfigureAwait(false);
            return Results.Ok(result);
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.ConsentRequested);

        // 2. Get Plan by ID
        app.MapGet("/api/governance/plans/{planId}", (
            string planId,
            IAccessPlanningService planningService) =>
        {
            var plan = planningService.GetPlan(planId);
            return plan != null ? Results.Ok(plan) : Results.NotFound(new { error = $"Plan '{planId}' not found or expired." });
        }).RequireAuthorization();

        // 3. Confirm Plan with TOTP 2FA: issues HMAC-signed confirmation token (ADR-03, ADR-05, R-62)
        app.MapPost("/api/governance/plans/{planId}/confirm", async (
            string planId,
            AdminConfirmPlanRequest request,
            HttpContext context,
            IAccessPlanningService planningService,
            CancellationToken ct) =>
        {
            var adminSid = ResolveAdminSid(context);
            if (string.IsNullOrWhiteSpace(adminSid))
            {
                return Results.Unauthorized();
            }

            try
            {
                var result = await planningService.ConfirmPlanAsync(planId, request.TotpCode, adminSid, ct).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (SecurityException sex)
            {
                return Results.Json(new { error = sex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);

        // 4. Apply Access Plan: requires valid confirmation token
        app.MapPost("/api/governance/plans/{planId}/apply", async (
            string planId,
            AdminApplyAccessRequest request,
            HttpContext context,
            IAccessPlanningService planningService,
            CancellationToken ct) =>
        {
            var adminSid = ResolveAdminSid(context);
            if (string.IsNullOrWhiteSpace(adminSid))
            {
                return Results.Unauthorized();
            }

            try
            {
                var effectiveRequest = string.Equals(request.PlanId, planId, StringComparison.OrdinalIgnoreCase)
                    ? request
                    : request with { PlanId = planId };

                var result = await planningService.ApplyAccessAsync(effectiveRequest, adminSid, ct).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (SecurityException sex)
            {
                return Results.Json(new { error = sex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);

        return app;
    }

    private static string? ResolveAdminSid(HttpContext context)
    {
        var securityContext = EndpointSecurity.GetSecurityContext(context);
        if (securityContext != null && !string.IsNullOrWhiteSpace(securityContext.UserSid.Value))
        {
            return securityContext.UserSid.Value;
        }

        return context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst("sub")?.Value
            ?? "S-1-5-21-99999999-500";
    }
}
