namespace Autheris.Api.Endpoints;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

public static class HitLEndpoints
{
    public sealed record ApproveTicketRequest(string ApprovalId);
    public sealed record RejectTicketRequest(string ApprovalId, string? Reason);

    public static IEndpointRouteBuilder MapHitLEndpoints(this IEndpointRouteBuilder app)
    {
        // F-AI-05: HitL Step-Up Approval Endpoints
        app.MapGet("/api/governance/hitl/pending", (
            string? tenantId,
            HttpContext context,
            IHitLStepUpApprovalService hitlService,
            IOptions<GatewayOptions> options) =>
        {
            if (!options.Value.HitLStepUp.Enabled)
            {
                return Results.NotFound(new { error = "HitL step-up approval is disabled." });
            }

            // SEC C-05: Only approvers may list tickets, and only those of their own tenant.
            if (!EndpointSecurity.IsApprover(context.User))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var effectiveTenant = ResolveListingTenant(context, tenantId);
            if (effectiveTenant == null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var tickets = hitlService.GetPendingTickets(effectiveTenant);
            return Results.Ok(tickets);
        }).RequireAuthorization();

        app.MapPost("/api/governance/hitl/approve", async (
            ApproveTicketRequest request,
            HttpContext context,
            IHitLStepUpApprovalService hitlService,
            IOptions<GatewayOptions> options) =>
        {
            if (!options.Value.HitLStepUp.Enabled)
            {
                return Results.NotFound(new { error = "HitL step-up approval is disabled." });
            }

            if (!EndpointSecurity.IsApprover(context.User) || string.IsNullOrWhiteSpace(request.ApprovalId))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var approver = BuildApproverContext(context);
            if (approver == null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var forbidden = await EnsureTableApproverAsync(context, hitlService, request.ApprovalId, approver).ConfigureAwait(false);
            if (forbidden != null)
            {
                return forbidden;
            }

            var result = await hitlService.ApproveStepUpRequestAsync(request.ApprovalId, approver, context.RequestAborted);
            return result.IsApproved ? Results.Ok(result) : Results.BadRequest(result);
        }).RequireAuthorization();

        app.MapPost("/api/governance/hitl/reject", async (
            RejectTicketRequest request,
            HttpContext context,
            IHitLStepUpApprovalService hitlService,
            IOptions<GatewayOptions> options) =>
        {
            if (!options.Value.HitLStepUp.Enabled)
            {
                return Results.NotFound(new { error = "HitL step-up approval is disabled." });
            }

            if (!EndpointSecurity.IsApprover(context.User) || string.IsNullOrWhiteSpace(request.ApprovalId))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var approver = BuildApproverContext(context);
            if (approver == null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var forbidden = await EnsureTableApproverAsync(context, hitlService, request.ApprovalId, approver).ConfigureAwait(false);
            if (forbidden != null)
            {
                return forbidden;
            }

            var result = await hitlService.RejectStepUpRequestAsync(request.ApprovalId, approver, request.Reason, context.RequestAborted);
            return Results.Ok(result);
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// SEC C-05: Tenant used for listing pending tickets. Without a query tenant the caller's own tenant is used
    /// (never "all tenants"). A foreign tenant is only allowed for ClusterAdmin. Returns null when forbidden.
    /// </summary>
    internal static string? ResolveListingTenant(HttpContext context, string? requestedTenant)
    {
        var securityContext = EndpointSecurity.GetSecurityContext(context);
        var ownTenant = (securityContext?.TenantId ?? EndpointSecurity.GetRequestTenant(context)).Value;
        if (string.IsNullOrWhiteSpace(requestedTenant) ||
            string.Equals(requestedTenant.Trim(), ownTenant, StringComparison.OrdinalIgnoreCase))
        {
            return ownTenant;
        }

        var isClusterAdmin = securityContext?.IsClusterAdmin ?? EndpointSecurity.IsClusterAdmin(context.User);
        return isClusterAdmin ? requestedTenant.Trim() : null;
    }

    /// <summary>
    /// SEC C-05: Approver identity is derived with the same function as the requester identity (<c>GetUserSid()</c>);
    /// all further identifiers (oid, sub, upn, NameIdentifier, PrimarySid ...) are passed on for the self-approval check.
    /// </summary>
    internal static HitLApproverContext? BuildApproverContext(HttpContext context)
    {
        var securityContext = EndpointSecurity.GetSecurityContext(context);
        var principal = context.User;
        var approverSid = securityContext != null ? securityContext.UserSid.Value : principal.GetUserSid()?.Value;
        if (string.IsNullOrWhiteSpace(approverSid))
        {
            return null;
        }

        var identifiers = principal.GetUserIdentifiers().ToList();
        var tenant = (securityContext?.TenantId ?? EndpointSecurity.GetRequestTenant(context)).Value;
        var isClusterAdmin = securityContext?.IsClusterAdmin ?? EndpointSecurity.IsClusterAdmin(principal);
        return new HitLApproverContext(approverSid, identifiers, tenant, isClusterAdmin);
    }

    /// <summary>
    /// SEC C-05: Non-global approvers (DataSteward/DataOwner) must additionally be registered owner or delegate
    /// of the ticket's target table. GovernanceAdmin/ClusterAdmin are exempt.
    /// </summary>
    private static async Task<IResult?> EnsureTableApproverAsync(
        HttpContext context,
        IHitLStepUpApprovalService hitlService,
        string approvalId,
        HitLApproverContext approver)
    {
        if (EndpointSecurity.IsGlobalGovernanceAdmin(context.User))
        {
            return null;
        }

        var ticket = await hitlService.GetTicketAsync(approvalId, context.RequestAborted);
        if (ticket == null ||
            (!approver.IsCrossTenantAdmin && !string.Equals(ticket.TenantId, approver.TenantId, StringComparison.OrdinalIgnoreCase)))
        {
            // Let the service produce the uniform "not found" answer (no existence oracle for foreign tenants).
            return null;
        }

        var ownershipRepository = context.RequestServices.GetService<IDataOwnershipRepository>();
        if (ownershipRepository == null)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var isAuthorized = await ownershipRepository
            .IsAuthorizedApproverForTableAsync(ticket.TargetTable, new Sid(approver.ApproverSid), context.RequestAborted)
            .ConfigureAwait(false);

        return isAuthorized ? null : Results.StatusCode(StatusCodes.Status403Forbidden);
    }
}
