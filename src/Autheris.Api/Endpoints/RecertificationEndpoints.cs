namespace Autheris.Api.Endpoints;

using System;
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
    public sealed record CreateCampaignRequest(string Name, RecertClass RecertClass, DateTimeOffset? Deadline = null);
    public sealed record ItemDecisionRequest(string? Comments);

    public static IEndpointRouteBuilder MapRecertificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenants/{tenantId}/governance/recert")
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin);

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
            var campaign = await service.CreateCampaignAsync(new TenantId(tenantId), request.Name, request.RecertClass, request.Deadline, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/tenants/{tenantId}/governance/recert/campaigns/{campaign.CampaignId}", campaign);
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

        group.MapPost("/campaigns/{campaignId}/items/{itemId}/certify", async (
            string tenantId,
            string campaignId,
            string itemId,
            ItemDecisionRequest request,
            HttpContext httpContext,
            IRecertificationService service,
            CancellationToken ct) =>
        {
            var reviewerId = httpContext.User.GetUserSid()?.Value ?? "REVIEWER";
            try
            {
                await service.CertifyItemAsync(new TenantId(tenantId), campaignId, itemId, reviewerId, request?.Comments, ct).ConfigureAwait(false);
                return Results.Ok(new { message = "Item successfully certified." });
            }
            catch (SecurityException)
            {
                return Results.Problem(
                    detail: "Self-review is strictly forbidden.",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Anti-self-review violation (SEC-SC-15)");
            }
        }).WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);

        group.MapPost("/campaigns/{campaignId}/items/{itemId}/revoke", async (
            string tenantId,
            string campaignId,
            string itemId,
            ItemDecisionRequest request,
            HttpContext httpContext,
            IRecertificationService service,
            CancellationToken ct) =>
        {
            var reviewerId = httpContext.User.GetUserSid()?.Value ?? "REVIEWER";
            await service.RevokeItemAsync(new TenantId(tenantId), campaignId, itemId, reviewerId, request?.Comments, ct).ConfigureAwait(false);
            return Results.Ok(new { message = "Item successfully revoked." });
        }).WithAudit(AuditLevel.Full, AuditEventTypes.ConsentRevoked);

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
}
