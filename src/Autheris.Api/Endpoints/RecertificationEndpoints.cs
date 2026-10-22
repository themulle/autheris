namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Governance.Recertification;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Autheris.Api.Extensions;
using Autheris.Domain.Audit;

public static class RecertificationEndpoints
{
    public sealed record CreateCampaignRequest(string Name, RecertClass RecertClass, DateTimeOffset? Deadline = null, string? AssetType = null);
    public sealed record ItemDecisionRequest(string? Comments);

    public static IEndpointRouteBuilder MapRecertificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenants/{tenantId}/governance/recert")
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
            .RequireTenantRouteMatch();

        group.MapGet("/campaigns", async (
            string tenantId,
            IRecertificationService service,
            CancellationToken ct) =>
        {
            var campaigns = await service.GetCampaignsAsync(new TenantId(tenantId), ct).ConfigureAwait(false);
            return Results.Ok(campaigns);
        }).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapPost("/campaigns", async (
            string tenantId,
            CreateCampaignRequest request,
            IRecertificationService service,
            CancellationToken ct) =>
        {
            try
            {
                var campaign = await service.CreateCampaignAsync(new TenantId(tenantId), request.Name, request.RecertClass, request.Deadline, request.AssetType, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/tenants/{tenantId}/governance/recert/campaigns/{campaign.CampaignId}", campaign);
            }
            catch (ArgumentException ex) when (ex.Message.Contains("UNSUPPORTED_ASSET_TYPE"))
            {
                return Results.Problem(
                    detail: "Recertification campaigns requesting the AccessPolicy asset type are not supported.",
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "UNSUPPORTED_ASSET_TYPE");
            }
        }).WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        group.MapGet("/campaigns/{campaignId}/items", async (
            string tenantId,
            string campaignId,
            IRecertificationService service,
            CancellationToken ct) =>
        {
            var items = await service.GetItemsAsync(new TenantId(tenantId), campaignId, ct).ConfigureAwait(false);
            return Results.Ok(items);
        }).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapPost("/campaigns/{campaignId}/items/{itemId}/certify", (
            string tenantId,
            string campaignId,
            string itemId,
            ItemDecisionRequest request,
            HttpContext httpContext,
            IRecertificationService service,
            CancellationToken ct) => HandleCertifyAsync(tenantId, campaignId, itemId, request, httpContext, service, ct))
            .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);

        group.MapPost("/campaigns/{campaignId}/items/{itemId}/revoke", (
            string tenantId,
            string campaignId,
            string itemId,
            ItemDecisionRequest request,
            HttpContext httpContext,
            IRecertificationService service,
            CancellationToken ct) => HandleRevokeAsync(tenantId, campaignId, itemId, request, httpContext, service, ct))
            .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentRevoked);

        group.MapPost("/cycle", async (
            string tenantId,
            IRecertificationService service,
            CancellationToken ct) =>
        {
            var count = await service.ExecuteAutoRevocationCycleAsync(DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            return Results.Ok(new { revokedItemsCount = count });
        }).WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        return app;
    }

    internal static async Task<IResult> HandleCertifyAsync(
        string tenantId, string campaignId, string itemId, ItemDecisionRequest? request,
        HttpContext httpContext, IRecertificationService service, CancellationToken ct)
    {
        // GOV-02: no SID means no reviewer identity; fail closed instead of falling back to a constant.
        var reviewerId = httpContext.User.GetUserSid()?.Value;
        if (string.IsNullOrWhiteSpace(reviewerId))
        {
            return NoReviewerIdentity();
        }

        try
        {
            await service.CertifyItemAsync(new TenantId(tenantId), campaignId, itemId, reviewerId, request?.Comments, ct).ConfigureAwait(false);
            return Results.Ok(new { message = "Item successfully certified." });
        }
        catch (SecurityException)
        {
            return Results.Problem(
                detail: "Only the designated reviewer may decide this item, and self-review is strictly forbidden.",
                statusCode: StatusCodes.Status403Forbidden,
                title: "Anti-self-review violation (SEC-SC-15)");
        }
        catch (RecertificationConflictException ex)
        {
            return Conflict(ex);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }

    internal static async Task<IResult> HandleRevokeAsync(
        string tenantId, string campaignId, string itemId, ItemDecisionRequest? request,
        HttpContext httpContext, IRecertificationService service, CancellationToken ct)
    {
        var reviewerId = httpContext.User.GetUserSid()?.Value;
        if (string.IsNullOrWhiteSpace(reviewerId))
        {
            return NoReviewerIdentity();
        }

        try
        {
            await service.RevokeItemAsync(new TenantId(tenantId), campaignId, itemId, reviewerId, request?.Comments, ct).ConfigureAwait(false);
            return Results.Ok(new { message = "Item successfully revoked." });
        }
        catch (SecurityException)
        {
            return Results.Problem(
                detail: "Only the designated reviewer may decide this item.",
                statusCode: StatusCodes.Status403Forbidden,
                title: "Reviewer not authorised");
        }
        catch (RecertificationConflictException ex)
        {
            return Conflict(ex);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static IResult NoReviewerIdentity() => Results.Problem(
        detail: "The caller has no reviewer identity (SID).",
        statusCode: StatusCodes.Status403Forbidden,
        title: "Reviewer identity required");

    private static IResult Conflict(RecertificationConflictException ex) => Results.Problem(
        detail: "The request conflicts with the current state of the recertification item or campaign.",
        statusCode: StatusCodes.Status409Conflict,
        title: ex.Code);
}
