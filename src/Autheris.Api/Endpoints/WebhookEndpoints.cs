namespace Autheris.Api.Endpoints;

using Autheris.Api.Errors;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Application.Catalog.Semantic;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app, GatewayOptions gatewayOptions)
    {
        app.MapPost("/api/webhooks/openmetadata", HandleOpenMetadataWebhookAsync)
           .AllowAnonymous()
           .WithRequestBodyLimit(2 * 1024 * 1024)
           .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        // dbt Cloud signs the body with HMAC-SHA256; it cannot send a bearer token, so the route is anonymous and the
        // signature is verified here, in core, before any module code runs. Without the dbt module the route answers 404.
        app.MapPost("/api/webhooks/dbt-cloud", HandleDbtCloudWebhookAsync)
           .AllowAnonymous()
           .WithRequestBodyLimit(DbtCloudWebhookMaxBytes)
           .WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        return app;
    }

    private const int DbtCloudWebhookMaxBytes = 2 * 1024 * 1024;
    private const int Sha256HexLength = 64;

    internal static async Task<IResult> HandleDbtCloudWebhookAsync(HttpContext context, CancellationToken ct = default)
    {
        // Step 1: the module decides presence; an absent module answers 404 before any header, body or option is read.
        if (context.RequestServices.GetService<IServiceProviderIsService>()?.IsService(typeof(IDbtCloudWebhookHandler)) != true)
        {
            return Results.NotFound();
        }

        // Step 2: signature header, in a fixed order. Missing and invalid signatures share one fixed 401 body.
        var (signatureHeader, allowPrefix) = SelectDbtCloudSignatureHeader(context.Request.Headers);
        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return DbtCloudUnauthorized();
        }

        // Step 3: bounded read (2 MB cap for an unauthenticated webhook).
        var (payload, tooLarge) = await EndpointSecurity.TryReadBodyAsync(
            context.Request,
            DbtCloudWebhookMaxBytes,
            "Webhook payload exceeds maximum allowed size (2 MB).",
            ct);
        if (payload == null)
        {
            return tooLarge!;
        }

        // Step 4: constant-time HMAC verification against the configured secret only.
        var options = context.RequestServices.GetRequiredService<IOptions<GatewayOptions>>().Value;
        var bypass = options.IsWebhookSignatureBypassed;
        if (bypass)
        {
            context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(WebhookEndpoints).FullName!)
                .LogWarning("Dbt webhook signature validation is bypassed by dangerous configuration.");
        }

        if (!DbtCloudWebhookSignature.Verify(payload, signatureHeader, options.Dbt.WebhookSecret, bypass, allowPrefix))
        {
            context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(WebhookEndpoints).FullName!)
                .LogWarning("Unauthorized dbt Cloud webhook call: invalid HMAC signature.");
            return DbtCloudUnauthorized();
        }

        // Step 5: only a verified payload reaches the module; the handler is resolved here, never earlier.
        var handler = context.RequestServices.GetRequiredService<IDbtCloudWebhookHandler>();
        var result = await handler.HandleVerifiedAsync(payload, ct);
        return result.Success
            ? Results.Ok(result)
            : Results.Json(result, statusCode: StatusCodes.Status401Unauthorized);
    }

    private static IResult DbtCloudUnauthorized() =>
        Results.Json(new DbtWebhookProcessingResult(false, "Invalid or missing HMAC signature."), statusCode: StatusCodes.Status401Unauthorized);

    private static (string? Value, bool AllowPrefix) SelectDbtCloudSignatureHeader(IHeaderDictionary headers)
    {
        if (headers.TryGetValue("X-Dbt-Signature", out var dbtSig) && !string.IsNullOrWhiteSpace(dbtSig))
        {
            return (dbtSig.ToString(), true);
        }

        if (headers.TryGetValue("X-Hub-Signature-256", out var hubSig) && !string.IsNullOrWhiteSpace(hubSig))
        {
            return (hubSig.ToString(), true);
        }

        // dbt Cloud sends the bare hex digest in Authorization. Anything with a scheme (Bearer, Basic, ...) is not a signature.
        var authorization = headers.Authorization.ToString();
        return IsBareSha256Hex(authorization) ? (authorization, false) : (null, false);
    }

    private static bool IsBareSha256Hex(string value)
    {
        if (value.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private const int OpenMetadataWebhookMaxBytes = 2 * 1024 * 1024;

    internal static async Task<IResult> HandleOpenMetadataWebhookAsync(HttpContext context, CancellationToken ct = default)
    {
        // Step 1: the module decides presence; an absent module answers 404 before any header, body or option is read.
        if (context.RequestServices.GetService<IServiceProviderIsService>()?.IsService(typeof(IOpenMetadataWebhookHandler)) != true)
        {
            return Results.NotFound();
        }

        // Step 2: signature header (bounded before the body is read). Each header is tested separately, so an empty
        // primary header falls through to the second one. A header is required even when the bypass flag is set.
        var signature = SelectOpenMetadataSignatureHeader(context.Request.Headers);
        if (string.IsNullOrWhiteSpace(signature))
        {
            return OpenMetadataUnauthorized();
        }

        // Step 3: bounded read (2 MB cap for an unauthenticated webhook).
        var (payload, tooLarge) = await EndpointSecurity.TryReadBodyAsync(
            context.Request,
            OpenMetadataWebhookMaxBytes,
            "Payload size exceeds maximum allowed size (2 MB).",
            ct);
        if (payload == null)
        {
            return tooLarge!;
        }

        // Step 4: constant-time HMAC verification against the configured secret only.
        var options = context.RequestServices.GetRequiredService<IOptions<GatewayOptions>>().Value;
        var bypass = options.IsWebhookSignatureBypassed;
        var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(WebhookEndpoints).FullName!);
        if (bypass)
        {
            logger?.LogWarning("OpenMetadata webhook signature validation is bypassed by dangerous configuration.");
        }

        if (!OpenMetadataWebhookSignature.Verify(payload, signature, options.OpenMetadata.WebhookSecret, bypass, logger))
        {
            logger?.LogWarning("Unauthorized OpenMetadata webhook call: invalid HMAC signature.");
            return OpenMetadataUnauthorized();
        }

        // Step 5: only a verified payload reaches the module; the handler is resolved here, never earlier.
        var handler = context.RequestServices.GetRequiredService<IOpenMetadataWebhookHandler>();
        var success = await handler.HandleVerifiedAsync(payload, ct);
        return success
            ? Results.Ok(new { status = "Processed" })
            : ApiProblems.BadRequest(ApiErrorCodes.WebhookSignatureOrFormatInvalid, "Failed to process webhook.");
    }

    private static IResult OpenMetadataUnauthorized() =>
        Results.Json(new { error = "Invalid or missing HMAC signature." }, statusCode: StatusCodes.Status401Unauthorized);

    private static string? SelectOpenMetadataSignatureHeader(IHeaderDictionary headers)
    {
        if (headers.TryGetValue("X-OpenMetadata-Signature", out var primary) && !string.IsNullOrWhiteSpace(primary))
        {
            return primary.ToString();
        }

        return headers.TryGetValue("X-OM-Signature", out var secondary) && !string.IsNullOrWhiteSpace(secondary)
            ? secondary.ToString()
            : null;
    }
}
