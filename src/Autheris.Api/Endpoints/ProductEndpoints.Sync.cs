namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
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
            .RequireAuthorization(GatewayPolicies.ProductSync)
            .WithRequestBodyLimit(MaxSyncBodyBytes)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        group.MapPost("sync/apply", (HttpContext context, ProductSyncService service) => SyncAsync(context, service, apply: true))
            .RequireAuthorization(GatewayPolicies.ProductSync)
            .WithRequestBodyLimit(MaxSyncBodyBytes)
            .WithAudit(AuditLevel.Full, AuditEventTypes.ProductSyncApplied);
        app.MapPost("/api/v1/products/sync/webhook/{tenant}", (string tenant, HttpContext context, ProductSyncWebhookService service) => WebhookAsync(tenant, context, service))
            .AllowAnonymous()
            .WithRequestBodyLimit(ProductSyncWebhookService.MaxBodyBytes)
            .WithAudit(AuditLevel.Delegated, AuditEventTypes.ProductSyncWebhookReceived);
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

        if (apply && context.User.IsReadOnly())
        {
            return ApiProblems.Forbidden(ProductErrorCodes.CreationNotAuthorized, "A read-only token cannot apply a synchronisation.", context);
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

        if (!TryReadWebhookDelivery(context.Request.Headers, out var delivery))
        {
            return Results.Unauthorized();
        }

        var status = await service.HandleAsync(tenantId, body, delivery, context.RequestAborted).ConfigureAwait(false);
        return status switch
        {
            ProductSyncWebhookStatus.Accepted => Results.Json(new { status = "Accepted", message = "Delivery accepted" }, statusCode: StatusCodes.Status202Accepted),
            ProductSyncWebhookStatus.Duplicate => ApiProblems.Conflict("PRODUCT_SYNC_REPLAY", "The delivery was already processed.", context),
            ProductSyncWebhookStatus.BadRequest => ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The webhook body is invalid.", context),
            ProductSyncWebhookStatus.TooLarge => ApiProblems.PayloadTooLarge("The webhook body is too large.", context),
            ProductSyncWebhookStatus.Unavailable => ApiProblems.ServiceUnavailable(detail: "The webhook replay store is unavailable.", httpContext: context),
            _ => Results.Unauthorized()
        };
    }

    internal static bool TryReadWebhookDelivery(IHeaderDictionary headers, out ProductSyncWebhookDelivery delivery)
    {
        var hasAutheris = TryReadHeaderTuple(
            headers,
            "X-Autheris-Timestamp",
            "X-Autheris-Delivery",
            "X-Autheris-Signature-256",
            out var autherisTuple,
            out var autherisPresent);
        var hasWebhook = TryReadHeaderTuple(
            headers,
            "X-Webhook-Timestamp",
            "X-Webhook-Delivery",
            "X-Webhook-Signature-256",
            out var webhookTuple,
            out var webhookPresent);

        if (!hasAutheris || !hasWebhook || (autherisPresent && webhookPresent))
        {
            delivery = default!;
            return false;
        }

        delivery = webhookPresent ? webhookTuple : autherisTuple;
        return true;
    }

    private static bool TryReadHeaderTuple(
        IHeaderDictionary headers,
        string timestampHeader,
        string deliveryHeader,
        string signatureHeader,
        out ProductSyncWebhookDelivery delivery,
        out bool anyPresent)
    {
        var ts = headers[timestampHeader];
        var id = headers[deliveryHeader];
        var sig = headers[signatureHeader];
        anyPresent = ts.Count > 0 || id.Count > 0 || sig.Count > 0;

        if (ts.Count > 1 || id.Count > 1 || sig.Count > 1)
        {
            delivery = default!;
            return false;
        }

        delivery = new ProductSyncWebhookDelivery(ts.ToString(), id.ToString(), sig.ToString());
        return true;
    }
}
