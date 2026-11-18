namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Application.Products;
using Autheris.Application.Products.Sync;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// Git synchronisation routes. Plan and apply are callable only by the <c>DataProductSync</c> identity of the authenticated tenant;
/// the webhook is anonymous and authenticated by its signature, with the tenant bound by the route.
/// </summary>
public static partial class ProductEndpoints
{
    private const int MaxSyncBodyBytes = 4 * 1024 * 1024;

    private static void MapSyncRoutes(IEndpointRouteBuilder app, RouteGroupBuilder group)
    {
        group.MapPost("sync/plan", (HttpContext context, ProductSyncService service) => SyncAsync(context, service, apply: false))
            .WithRequestBodyLimit(MaxSyncBodyBytes)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        group.MapPost("sync/apply", (HttpContext context, ProductSyncService service) => SyncAsync(context, service, apply: true))
            .WithRequestBodyLimit(MaxSyncBodyBytes)
            .WithAudit(AuditLevel.Full, AuditEventTypes.ProductSyncApplied);
        app.MapPost("/api/v1/products/sync/webhook/{tenant}", (string tenant, HttpContext context, ProductSyncWebhookService service) => WebhookAsync(tenant, context, service))
            .AllowAnonymous()
            .WithRequestBodyLimit(ProductSyncWebhookService.MaxBodyBytes)
            .WithAudit(AuditLevel.Full, AuditEventTypes.ProductSyncWebhookReceived);
    }

    internal sealed record SyncFileBody(string? Path, string? Content, bool Symlink = false);

    internal sealed record SyncBody(string? Commit, List<SyncFileBody>? Files);

    internal static async Task<IResult> SyncAsync(HttpContext context, ProductSyncService service, bool apply)
    {
        var security = EndpointSecurity.GetSecurityContext(context);
        if (!security.HasRole(GatewayRole.DataProductSync))
        {
            return ApiProblems.Forbidden(ProductErrorCodes.CreationNotAuthorized, "Only the synchronisation identity may call the synchronisation endpoints.", context);
        }

        var caller = new ProductCaller(security.UserSid, security.TenantId, false, ViaSyncPath: true);
        return await MapErrorsAsync(async () =>
        {
            var body = await ReadAsync<SyncBody>(context).ConfigureAwait(false);
            var request = new ProductSyncRequest(
                caller,
                body.Commit ?? string.Empty,
                (body.Files ?? []).Select(static f => new ProductSyncFile(f.Path ?? string.Empty, f.Content ?? string.Empty, f.Symlink)).ToList());
            var result = apply
                ? await service.ApplyAsync(request, context.RequestAborted).ConfigureAwait(false)
                : await service.PlanAsync(request, context.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new
            {
                commit = result.Commit,
                applied = result.Applied,
                created = result.Count(ProductSyncAction.Create),
                changed = result.Count(ProductSyncAction.Change),
                unchanged = result.Count(ProductSyncAction.Unchanged),
                retired = result.Count(ProductSyncAction.Retire),
                drift = result.Count(ProductSyncAction.Drift),
                items = result.Items.Select(static i => new { schema = i.Schema, name = i.Name, action = i.Action.ToString(), reason = i.Reason })
            });
        }, context).ConfigureAwait(false);
    }

    internal static async Task<IResult> WebhookAsync(string tenant, HttpContext context, ProductSyncWebhookService service)
    {
        if (!TenantId.TryParse(tenant, out var tenantId))
        {
            return Results.Unauthorized();
        }

        byte[] body;
        try
        {
            body = await context.Request.ReadBodyAsBytesAsync(ProductSyncWebhookService.MaxBodyBytes, context.RequestAborted).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return ApiProblems.PayloadTooLarge("The webhook body is too large.", context);
        }

        var headers = context.Request.Headers;
        var delivery = new ProductSyncWebhookDelivery(
            headers["X-Autheris-Timestamp"].ToString(), headers["X-Autheris-Delivery"].ToString(), headers["X-Autheris-Signature-256"].ToString());
        var status = await service.HandleAsync(tenantId, body, delivery, context.RequestAborted).ConfigureAwait(false);
        return status switch
        {
            ProductSyncWebhookStatus.Accepted => Results.Json(new { status = "Accepted", message = "Sync triggered" }, statusCode: StatusCodes.Status202Accepted),
            ProductSyncWebhookStatus.Duplicate => ApiProblems.Conflict("PRODUCT_SYNC_REPLAY", "The delivery was already processed.", context),
            ProductSyncWebhookStatus.BadRequest => ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The webhook body is invalid.", context),
            ProductSyncWebhookStatus.TooLarge => ApiProblems.PayloadTooLarge("The webhook body is too large.", context),
            ProductSyncWebhookStatus.Unavailable => ApiProblems.ServiceUnavailable(detail: "The webhook replay store is unavailable.", httpContext: context),
            _ => Results.Unauthorized()
        };
    }
}
