namespace Autheris.Api.Endpoints;

using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Endpoints.Support;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Extensions.Cdc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class StreamingCdcEndpoints
{
    public static IEndpointRouteBuilder MapStreamingCdcEndpoints(this IEndpointRouteBuilder app)
    {
        // CDC & Realtime Streaming Ingestion Endpoint (P5)
        app.MapPost("/api/v1/cdc/events", async (
            HttpRequest request,
            ICdcEventIngestionService ingestionService,
            ILoggerFactory loggerFactory) =>
        {
            var user = request.HttpContext.User;

            var isClusterAdmin = IsCdcClusterAdmin(user);
            if (!IsAuthorizedCdcIngestion(user))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // SEC M-07: Bounded read (Content-Length alone is bypassable via chunked transfer encoding).
            var (body, tooLarge) = await EndpointSecurity.TryReadBodyAsync(
                request,
                10 * 1024 * 1024,
                "CDC payload exceeds maximum allowed size (10 MB).",
                request.HttpContext.RequestAborted);
            if (tooLarge != null)
            {
                return tooLarge;
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return Results.BadRequest(new { error = "Empty CDC payload" });
            }

            try
            {
                // A-1: caller tenant comes from the resolved request context (same source as the rest of the pipeline).
                var callerTenant = EndpointSecurity.GetRequestTenant(request.HttpContext).Value;

                var cdcEvent = DebeziumCdcParser.Parse(body);

                // A-1: even canonical cluster admins must name the target tenant explicitly (no implicit/null tenant).
                if (isClusterAdmin && string.IsNullOrWhiteSpace(cdcEvent.TenantId))
                {
                    return Results.BadRequest(new { error = "CDC event must carry an explicit tenant" });
                }

                // SEC-4: Enforce strict fail-closed tenant isolation on ingested CDC events
                if (!isClusterAdmin)
                {
                    if (string.IsNullOrWhiteSpace(callerTenant))
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }

                    if (!string.IsNullOrWhiteSpace(cdcEvent.TenantId) &&
                        !string.Equals(cdcEvent.TenantId, callerTenant, StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }

                    if (string.IsNullOrWhiteSpace(cdcEvent.TenantId))
                    {
                        cdcEvent = cdcEvent with { TenantId = callerTenant };
                    }
                }

                await ingestionService.PublishEventAsync(cdcEvent, request.HttpContext.RequestAborted);
                return Results.Accepted(value: new { status = "Ingested", eventId = cdcEvent.EventId });
            }
            catch (Exception ex)
            {
                var logger = loggerFactory.CreateLogger("Autheris.CdcEndpoint");
                logger.LogWarning(ex, "Failed to parse or ingest CDC event payload.");
                return Results.BadRequest(new { error = "Invalid CDC event format" });
            }
        }).RequireAuthorization()
          .WithRequestBodyLimit(10 * 1024 * 1024) // SEC M-01: explicit large-body exception to the global Kestrel limit
          .WithAudit(AuditLevel.Full, AuditEventTypes.StreamSubscribe);

        // F-EVT-01: CloudEvents Outbound Webhook Subscriptions (interim guard WP-ARC-A-01: 501 unless
        // Gateway:CdcWebhooks:Enabled is true; the subscription store is not registered while disabled).
        app.MapGet("/api/v1/cdc/subscriptions", (Delegate)ListSubscriptionsAsync)
            .RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        app.MapPost("/api/v1/cdc/subscriptions", CreateSubscriptionAsync)
            .RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        app.MapDelete("/api/v1/cdc/subscriptions/{id}", DeleteSubscriptionAsync)
            .RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        return app;
    }

    /// <summary>Returns a 501 problem result while the CDC webhook feature flag is off; otherwise <c>null</c>.</summary>
    internal static IResult? GuardCdcWebhooksDisabled(HttpContext context)
    {
        var options = context.RequestServices.GetService<IOptions<CdcWebhooksOptions>>()?.Value;
        if (options is { Enabled: true })
        {
            return null;
        }

        return Results.Problem(
            title: "CDC webhook subscriptions are not enabled",
            detail: "Outbound CDC webhook delivery is not available. Set Gateway:CdcWebhooks:Enabled to true once delivery is supported.",
            statusCode: StatusCodes.Status501NotImplemented);
    }

    private static string ResolveTenant(HttpContext context)
    {
        var tenantId = EndpointSecurity.GetRequestTenant(context).Value;
        return string.IsNullOrWhiteSpace(tenantId) ? "default" : tenantId;
    }

    internal static async Task<IResult> ListSubscriptionsAsync(HttpContext context)
    {
        var guard = GuardCdcWebhooksDisabled(context);
        if (guard != null)
        {
            return guard;
        }

        if (!IsAuthorizedSubscriptionAdmin(context.User))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var store = context.RequestServices.GetRequiredService<ICloudEventSubscriptionStore>();
        var subs = await store.ListSubscriptionsAsync(ResolveTenant(context), context.RequestAborted);
        // SEC M-5: Never return HMAC secrets in GET responses
        var safeSubs = subs.Select(s => s with { HmacSecret = string.IsNullOrEmpty(s.HmacSecret) ? string.Empty : "[REDACTED]" });
        return Results.Ok(safeSubs);
    }

    internal static async Task<IResult> CreateSubscriptionAsync(HttpContext context, CloudEventWebhookSubscription subscription)
    {
        var guard = GuardCdcWebhooksDisabled(context);
        if (guard != null)
        {
            return guard;
        }

        if (!IsAuthorizedSubscriptionAdmin(context.User))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var store = context.RequestServices.GetRequiredService<ICloudEventSubscriptionStore>();
        var tenantId = ResolveTenant(context);

        if (!Uri.TryCreate(subscription.TargetUrl, UriKind.Absolute, out var targetUri))
        {
            return Results.BadRequest(new { error = "Invalid absolute target URL" });
        }

        try
        {
            await Autheris.Application.Security.EgressUrlPolicy.ValidateResolvedAsync(targetUri, isDev: false, ct: context.RequestAborted);
        }
        catch (Exception ex)
        {
            SafeErrorResults.LogFailure(context, ex, "CDC_TARGET_URL_NOT_ALLOWED");
            return SafeErrorResults.BadRequest("CDC_TARGET_URL_NOT_ALLOWED", "The target URL is not allowed.", context);
        }

        // SEC M-5: Server-side ID generation; client-chosen ID is ignored to prevent overwrite attacks
        var serverId = Guid.NewGuid().ToString("N");
        var securedSub = subscription with { Id = serverId, TenantId = tenantId };
        try
        {
            await store.RegisterSubscriptionAsync(securedSub, context.RequestAborted);
        }
        catch (InvalidOperationException ex)
        {
            SafeErrorResults.LogFailure(context, ex, "CDC_SUBSCRIPTION_QUOTA_EXCEEDED");
            return SafeErrorResults.TooManyRequests("CDC_SUBSCRIPTION_QUOTA_EXCEEDED", "The subscription limit has been reached.", context);
        }

        var responseSub = securedSub with { HmacSecret = string.IsNullOrEmpty(securedSub.HmacSecret) ? string.Empty : "[REDACTED]" };
        return Results.Created($"/api/v1/cdc/subscriptions/{securedSub.Id}", responseSub);
    }

    internal static async Task<IResult> DeleteSubscriptionAsync(string id, HttpContext context)
    {
        var guard = GuardCdcWebhooksDisabled(context);
        if (guard != null)
        {
            return guard;
        }

        if (!IsAuthorizedSubscriptionAdmin(context.User))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var store = context.RequestServices.GetRequiredService<ICloudEventSubscriptionStore>();
        var removed = await store.RemoveSubscriptionAsync(ResolveTenant(context), id, context.RequestAborted);
        return removed ? Results.NoContent() : Results.NotFound();
    }

    private static bool IsAuthorizedSubscriptionAdmin(ClaimsPrincipal user)
        => GatewayPolicies.HasAnyRole(user, [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin]);

    /// <summary>
    /// SEC H-04: Cross-tenant CDC ingestion is decided by roles only. The former substring check ("ADMIN" in SID or
    /// user name) promoted accounts such as "CORP\badminton" to cluster admin and has been removed.
    /// </summary>
    internal static bool IsCdcClusterAdmin(ClaimsPrincipal user)
        => EndpointSecurity.IsCanonicalClusterAdmin(user);

    // A-1: PlatformAdmin is a tenant-scoped administrator: authorized to ingest, but only into its own tenant.
    internal static bool IsAuthorizedCdcIngestion(ClaimsPrincipal user)
        => IsCdcClusterAdmin(user) ||
           user.IsInRole("CdcIngestionService") ||
           user.IsInRole("PlatformAdmin") ||
           GatewayPolicies.HasAnyRole(user, [GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin]);
}
