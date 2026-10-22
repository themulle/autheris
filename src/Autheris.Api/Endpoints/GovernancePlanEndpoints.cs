namespace Autheris.Api.Endpoints;

using System;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
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
            AdminPlanAccessRequest? request,
            HttpContext context,
            IAccessPlanningService planningService,
            CancellationToken ct) =>
        {
            var adminSid = ResolveAdminSid(context);
            if (string.IsNullOrWhiteSpace(adminSid))
            {
                return Results.Unauthorized();
            }

            if (request == null || string.IsNullOrWhiteSpace(request.DatasetId) || request.Grants == null)
            {
                return Results.BadRequest(new { error = "DatasetId and Grants are required." });
            }

            var result = await planningService.PlanAccessAsync(request, adminSid, EndpointSecurity.GetRequestTenant(context).Value, ct, context.User).ConfigureAwait(false);
            return Results.Ok(result);
        }).RequireAuthorization(GatewayPolicies.GovernanceAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.ConsentRequested);

        // 2. Get Plan by ID
        app.MapGet("/api/governance/plans/{planId}", async (
            string planId,
            IAccessPlanningService planningService,
            CancellationToken ct) =>
        {
            var plan = await planningService.GetPlanAsync(planId, ct).ConfigureAwait(false);
            return plan != null ? Results.Ok(plan) : Results.NotFound(new { error = $"Plan '{planId}' not found or expired." });
        }).RequireAuthorization(GatewayPolicies.GovernanceAdmin).WithAudit(AuditLevel.Summarized, AuditEventTypes.AccessPlanRead);

        // 3. Confirm Plan with TOTP 2FA: issues HMAC-signed confirmation token (ADR-03, ADR-05, R-62)
        app.MapPost("/api/governance/plans/{planId}/confirm", async (
            string planId,
            AdminConfirmPlanRequest? request,
            HttpContext context,
            IAccessPlanningService planningService,
            CancellationToken ct) =>
        {
            var adminSid = ResolveAdminSid(context);
            if (string.IsNullOrWhiteSpace(adminSid))
            {
                return Results.Unauthorized();
            }

            if (request == null || string.IsNullOrWhiteSpace(request.TotpCode))
            {
                return Results.BadRequest(new { error = "TotpCode is required." });
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
        }).RequireAuthorization(GatewayPolicies.GovernanceAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);

        // 4. Apply Access Plan: requires valid confirmation token
        app.MapPost("/api/governance/plans/{planId}/apply", async (
            string planId,
            AdminApplyAccessRequest? request,
            HttpContext context,
            IAccessPlanningService planningService,
            CancellationToken ct) =>
        {
            var adminSid = ResolveAdminSid(context);
            if (string.IsNullOrWhiteSpace(adminSid))
            {
                return Results.Unauthorized();
            }

            if (request == null || string.IsNullOrWhiteSpace(request.ConfirmationToken))
            {
                return Results.BadRequest(new { error = "ConfirmationToken is required." });
            }

            try
            {
                var effectiveRequest = string.Equals(request.PlanId, planId, StringComparison.OrdinalIgnoreCase)
                    ? request
                    : request with { PlanId = planId };

                var callerTenant = EndpointSecurity.GetRequestTenant(context);
                var result = await planningService.ApplyAccessAsync(effectiveRequest, adminSid, callerTenant, ct: ct, caller: context.User).ConfigureAwait(false);
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
        }).RequireAuthorization(GatewayPolicies.GovernanceAdmin).WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);

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
            ?? context.User.FindFirst("sub")?.Value;
    }
}
