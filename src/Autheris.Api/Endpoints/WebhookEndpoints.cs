namespace Autheris.Api.Endpoints;

using Autheris.Api.Errors;
using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Domain.Audit;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app, GatewayOptions gatewayOptions)
    {
        app.MapPost("/api/webhooks/openmetadata", HandleOpenMetadataWebhookAsync)
           .AllowAnonymous()
           .WithRequestBodyLimit(2 * 1024 * 1024)
           .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        return app;
    }

    internal static async Task<IResult> HandleOpenMetadataWebhookAsync(
        HttpContext context,
        IOpenMetadataSyncService syncService)
    {
        string? signature = context.Request.Headers["X-OpenMetadata-Signature"].FirstOrDefault() ??
                            context.Request.Headers["X-OM-Signature"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(signature))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.WebhookSignatureOrFormatInvalid, "Missing signature header.");
        }

        // SEC M-07: Bounded read instead of a bypassable Content-Length check (capped at 2 MB for unauthenticated webhook).
        var (payload, tooLarge) = await EndpointSecurity.TryReadBodyAsync(
            context.Request,
            2 * 1024 * 1024,
            "Payload size exceeds maximum allowed size (2 MB).",
            context.RequestAborted);
        if (payload == null)
        {
            return tooLarge!;
        }

        var success = await syncService.HandleWebhookEventAsync(payload, signature, context.RequestAborted);
        if (!success)
        {
            return ApiProblems.BadRequest(ApiErrorCodes.WebhookSignatureOrFormatInvalid, "Failed to process webhook or invalid signature.");
        }

        return Results.Ok(new { status = "Processed" });
    }
}
