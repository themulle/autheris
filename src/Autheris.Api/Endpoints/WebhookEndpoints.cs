namespace Autheris.Api.Endpoints;

using Autheris.Api.Errors;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Endpoints.Support;
using Autheris.Api.Extensions;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

public static class WebhookEndpoints
{
    /// <summary>EP-14: reads the EventGrid validation code defensively; false when the body is not a well-formed handshake.</summary>
    internal static bool TryReadValidationCode(string payload, out string? validationCode)
    {
        validationCode = null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (TryGetCode(item, out validationCode))
                    {
                        return true;
                    }
                }

                return true;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            TryGetCode(root, out validationCode);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }

        static bool TryGetCode(JsonElement element, out string? code)
        {
            code = null;
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("validationCode", out var vc)
                && vc.ValueKind == JsonValueKind.String)
            {
                code = vc.GetString();
                return true;
            }

            return false;
        }
    }

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app, GatewayOptions gatewayOptions)
    {
        app.MapPost("/api/webhooks/openmetadata", async (
            HttpContext context,
            IOpenMetadataSyncService syncService,
            IOptions<GatewayOptions> options) =>
        {
            // SEC M-07: Bounded read instead of a bypassable Content-Length check.
            var (payload, tooLarge) = await EndpointSecurity.TryReadBodyAsync(
                context.Request,
                2 * 1024 * 1024,
                "Payload size exceeds maximum allowed size (2 MB).",
                context.RequestAborted);
            if (payload == null)
            {
                return tooLarge!;
            }

            string? signature = context.Request.Headers["X-OpenMetadata-Signature"].FirstOrDefault() ??
                                context.Request.Headers["X-OM-Signature"].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(signature) && options.Value.IsWebhookSignatureBypassed)
            {
                signature = "bypassed";
            }

            var success = await syncService.HandleWebhookEventAsync(payload, signature, context.RequestAborted);
            if (!success)
            {
                return ApiProblems.BadRequest(ApiErrorCodes.WebhookSignatureOrFormatInvalid, "Failed to process webhook or invalid signature.");
            }

            return Results.Ok(new { status = "Processed" });
        }).AllowAnonymous().WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        // Canonical ITSM Webhook Endpoint
        app.MapPost("/api/webhooks/itsm/status-change", (
            HttpContext context,
            IItsmWebhookHandler webhookHandler,
            IOptions<GatewayOptions> opts) => ProcessItsmWebhookAsync(context, webhookHandler, opts, "ITSM"))
            .AllowAnonymous()
            .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);

        // Dedicated ServiceNow Webhook Endpoint
        app.MapPost("/api/webhooks/servicenow", (
            HttpContext context,
            IItsmWebhookHandler webhookHandler,
            IOptions<GatewayOptions> opts) => ProcessItsmWebhookAsync(context, webhookHandler, opts, "ServiceNow"))
            .AllowAnonymous()
            .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);

        // Dedicated Jira Webhook Endpoint
        app.MapPost("/api/webhooks/jira", (
            HttpContext context,
            IItsmWebhookHandler webhookHandler,
            IOptions<GatewayOptions> opts) => ProcessItsmWebhookAsync(context, webhookHandler, opts, "Jira"))
            .AllowAnonymous()
            .WithAudit(AuditLevel.Full, AuditEventTypes.ConsentApproved);

        // Real-Time Data Catalog Webhook Endpoint
        app.MapPost("/api/webhooks/catalog", async (
            HttpContext context,
            IDataCatalogWebhookHandler webhookHandler,
            IOptions<GatewayOptions> options) =>
        {
            var opts = options.Value;
            // SEC M-07: Bounded read instead of a bypassable Content-Length check.
            var (payload, tooLarge) = await EndpointSecurity.TryReadBodyAsync(
                context.Request,
                10 * 1024 * 1024,
                "Payload exceeds maximum allowed size (10 MB).",
                context.RequestAborted);
            if (payload == null)
            {
                return tooLarge!;
            }

            // Azure EventGrid SubscriptionValidation handshake
            if (context.Request.Headers.TryGetValue("Aeg-Event-Type", out var eventType) &&
                string.Equals(eventType.FirstOrDefault(), "SubscriptionValidation", StringComparison.OrdinalIgnoreCase))
            {
                // EP-14: malformed handshake bodies are a 400, never an unhandled exception on this anonymous route.
                return TryReadValidationCode(payload, out var validationCode)
                    ? Results.Ok(new { validationResponse = validationCode ?? "" })
                    : ApiProblems.BadRequest(ApiErrorCodes.WebhookPayloadInvalid, "Invalid EventGrid validation payload.");
            }

            string? signature = null;
            if (context.Request.Headers.TryGetValue("X-Catalog-Signature", out var sigHeader) ||
                context.Request.Headers.TryGetValue("X-Signature", out sigHeader) ||
                context.Request.Headers.TryGetValue("X-Hub-Signature-256", out sigHeader))
            {
                signature = sigHeader.FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(signature))
            {
                if (!opts.IsWebhookSignatureBypassed)
                {
                    return Results.Unauthorized();
                }
                signature = "bypassed";
            }

            // SEC M-34: The catalog handler signs "{unixSeconds}.{payload}", so senders typically transmit Unix seconds;
            // ISO 8601 remains accepted. The parsed value is passed on unchanged to the handler.
            DateTimeOffset? timestamp = null;
            if (context.Request.Headers.TryGetValue("X-Catalog-Timestamp", out var tsHeader) ||
                context.Request.Headers.TryGetValue("X-Timestamp", out tsHeader))
            {
                if (EndpointSecurity.TryParseWebhookTimestamp(tsHeader.FirstOrDefault(), out var ts))
                {
                    timestamp = ts;
                }
            }

            if (!opts.IsWebhookTimestampToleranceIgnored)
            {
                if (!timestamp.HasValue)
                {
                    return ApiProblems.Unauthorized(ApiErrorCodes.Unauthorized, "Missing required timestamp header (X-Catalog-Timestamp).");
                }

                if ((DateTimeOffset.UtcNow - timestamp.Value).Duration() > TimeSpan.FromMinutes(5))
                {
                    return ApiProblems.Unauthorized(ApiErrorCodes.Unauthorized, "Webhook timestamp is outside the permitted 5-minute tolerance window.");
                }
            }

            var provider = context.Request.Query.TryGetValue("provider", out var prov) ? prov.FirstOrDefault() : null;

            var result = await webhookHandler.HandleWebhookAsync(payload, signature, timestamp, provider, context.RequestAborted);
            if (!result.Success)
            {
                // INF-5: a processing failure is retryable (503); only rejected deliveries are 401.
                var status = result.Status == "PARTIALLY_FAILED" ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status401Unauthorized;
                return Results.Json(result, statusCode: status);
            }

            return Results.Ok(result);
        }).AllowAnonymous()
          .WithRequestBodyLimit(10 * 1024 * 1024) // SEC M-01: explicit large-body exception to the global Kestrel limit
          .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        app.MapPost("/api/v1/governance/catalog/webhook/{provider}", async (
            HttpContext context,
            string provider,
            IDataCatalogWebhookHandler webhookHandler,
            IOptions<GatewayOptions> options) =>
        {
            // SEC M-07: Bounded read instead of a bypassable Content-Length check.
            var (payload, tooLarge) = await EndpointSecurity.TryReadBodyAsync(
                context.Request,
                10 * 1024 * 1024,
                "Payload exceeds maximum allowed size (10 MB).",
                context.RequestAborted);
            if (payload == null)
            {
                return tooLarge!;
            }

            if (context.Request.Headers.TryGetValue("Aeg-Event-Type", out var eventType) &&
                string.Equals(eventType.FirstOrDefault(), "SubscriptionValidation", StringComparison.OrdinalIgnoreCase))
            {
                // EP-14: malformed handshake bodies are a 400, never an unhandled exception on this anonymous route.
                return TryReadValidationCode(payload, out var validationCode)
                    ? Results.Ok(new { validationResponse = validationCode ?? "" })
                    : ApiProblems.BadRequest(ApiErrorCodes.WebhookPayloadInvalid, "Invalid EventGrid validation payload.");
            }

            string? signature = null;
            if (context.Request.Headers.TryGetValue("X-Catalog-Signature", out var sigHeader) ||
                context.Request.Headers.TryGetValue("X-Signature", out sigHeader) ||
                context.Request.Headers.TryGetValue("X-Hub-Signature-256", out sigHeader))
            {
                signature = sigHeader.FirstOrDefault();
            }

            var opts = options.Value;
            if (string.IsNullOrWhiteSpace(signature))
            {
                if (!opts.IsWebhookSignatureBypassed)
                {
                    return Results.Unauthorized();
                }
                signature = "bypassed";
            }

            // SEC M-34: The catalog handler signs "{unixSeconds}.{payload}", so senders typically transmit Unix seconds;
            // ISO 8601 remains accepted. The parsed value is passed on unchanged to the handler.
            DateTimeOffset? timestamp = null;
            if (context.Request.Headers.TryGetValue("X-Catalog-Timestamp", out var tsHeader) ||
                context.Request.Headers.TryGetValue("X-Timestamp", out tsHeader))
            {
                if (EndpointSecurity.TryParseWebhookTimestamp(tsHeader.FirstOrDefault(), out var ts))
                {
                    timestamp = ts;
                }
            }

            if (!opts.IsWebhookTimestampToleranceIgnored)
            {
                if (!timestamp.HasValue)
                {
                    return ApiProblems.Unauthorized(ApiErrorCodes.Unauthorized, "Missing required timestamp header (X-Catalog-Timestamp).");
                }

                if ((DateTimeOffset.UtcNow - timestamp.Value).Duration() > TimeSpan.FromMinutes(5))
                {
                    return ApiProblems.Unauthorized(ApiErrorCodes.Unauthorized, "Webhook timestamp is outside the permitted 5-minute tolerance window.");
                }
            }

            var result = await webhookHandler.HandleWebhookAsync(payload, signature, timestamp, provider, context.RequestAborted);
            if (!result.Success)
            {
                // INF-5: a processing failure is retryable (503); only rejected deliveries are 401.
                var status = result.Status == "PARTIALLY_FAILED" ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status401Unauthorized;
                return Results.Json(result, statusCode: status);
            }

            return Results.Ok(result);
        }).AllowAnonymous()
          .WithRequestBodyLimit(10 * 1024 * 1024) // SEC M-01: explicit large-body exception to the global Kestrel limit
          .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        return app;
    }

    internal static async Task<IResult> ProcessItsmWebhookAsync(
        HttpContext context,
        IItsmWebhookHandler webhookHandler,
        IOptions<GatewayOptions> options,
        string defaultSystemName)
    {
        // SEC M-07: Bounded read instead of a bypassable Content-Length check.
        var (payload, tooLarge) = await EndpointSecurity.TryReadBodyAsync(
            context.Request,
            2 * 1024 * 1024,
            "Payload size exceeds maximum allowed size (2 MB).",
            context.RequestAborted);
        if (payload == null)
        {
            return tooLarge!;
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.WebhookPayloadInvalid, "Empty webhook payload.");
        }

        var opts = options.Value;
        string? signature = context.Request.Headers["X-ITSM-Signature"].FirstOrDefault()
                            ?? context.Request.Headers["X-ServiceNow-Signature"].FirstOrDefault()
                            ?? context.Request.Headers["X-Hub-Signature-256"].FirstOrDefault()
                            ?? context.Request.Headers["X-Hub-Signature"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(signature))
        {
            if (!opts.IsWebhookSignatureBypassed)
            {
                return Results.Unauthorized();
            }
            signature = "bypassed";
        }

        DateTimeOffset timestamp;
        var tsHeader = context.Request.Headers["X-ITSM-Timestamp"].FirstOrDefault()
                       ?? context.Request.Headers["X-Timestamp"].FirstOrDefault()
                       ?? context.Request.Headers["Date"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(tsHeader) || !EndpointSecurity.TryParseWebhookTimestamp(tsHeader, out timestamp))
        {
            if (!opts.IsWebhookTimestampToleranceIgnored)
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Header X-ITSM-Timestamp is required and must be a valid ISO 8601 timestamp or Unix time in seconds.");
            }
            timestamp = DateTimeOffset.UtcNow;
        }
        else if (!opts.IsWebhookTimestampToleranceIgnored)
        {
            var diff = (DateTimeOffset.UtcNow - timestamp).Duration();
            if (diff > TimeSpan.FromMinutes(5))
            {
                return Results.Unauthorized();
            }
        }

        try
        {
            using var jsonDoc = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            SafeErrorResults.LogFailure(context, ex, "WEBHOOK_PAYLOAD_INVALID");
            return SafeErrorResults.BadRequest("WEBHOOK_PAYLOAD_INVALID", "The webhook payload is invalid.", context);
        }

        // SEC H-06: The instance header is unsigned. It is only passed on for a consistency check;
        // the handler takes the instance exclusively from the signed payload.
        string? instanceHeader = context.Request.Headers["X-Instance-ID"].FirstOrDefault()
                                 ?? context.Request.Headers["X-ServiceNow-Instance"].FirstOrDefault()
                                 ?? context.Request.Headers["X-Jira-Instance"].FirstOrDefault()
                                 ?? context.Request.Query["instance"].FirstOrDefault();

        bool success;
        try
        {
            success = await webhookHandler.HandleStatusChangeAsync(payload, signature, timestamp, instanceHeader, tsHeader, context.RequestAborted);
        }
        catch (ArgumentException ex)
        {
            SafeErrorResults.LogFailure(context, ex, "WEBHOOK_SIGNATURE_OR_FORMAT_INVALID");
            return SafeErrorResults.BadRequest("WEBHOOK_SIGNATURE_OR_FORMAT_INVALID", "The webhook signature or payload format is invalid.", context);
        }
        catch (JsonException ex)
        {
            SafeErrorResults.LogFailure(context, ex, "WEBHOOK_PAYLOAD_INVALID");
            return SafeErrorResults.BadRequest("WEBHOOK_PAYLOAD_INVALID", "The webhook payload is invalid.", context);
        }
        catch (FormatException ex)
        {
            SafeErrorResults.LogFailure(context, ex, "WEBHOOK_SIGNATURE_OR_FORMAT_INVALID");
            return SafeErrorResults.BadRequest("WEBHOOK_SIGNATURE_OR_FORMAT_INVALID", "The webhook signature or payload format is invalid.", context);
        }

        if (!success)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(new { status = "Processed", system = defaultSystemName });
    }
}
