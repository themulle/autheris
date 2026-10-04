namespace Autheris.Api.Endpoints;

using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Extensions.Cdc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

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
                var callerTenant = user.FindFirst("tenant_id")?.Value
                                  ?? user.FindFirst("tid")?.Value
                                  ?? user.FindFirst("tenant")?.Value;

                var cdcEvent = DebeziumCdcParser.Parse(body);

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
          .WithRequestBodyLimit(10 * 1024 * 1024); // SEC M-01: explicit large-body exception to the global Kestrel limit

        // F-EVT-01: CloudEvents Outbound Webhook Subscriptions
        app.MapGet("/api/v1/cdc/subscriptions", async (
            HttpContext context,
            [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Events.Interfaces.ICloudEventSubscriptionStore store) =>
        {
            if (!IsAuthorizedSubscriptionAdmin(context.User))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var tenantId = EndpointSecurity.GetRequestTenant(context).Value;
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                tenantId = "default";
            }
            var subs = await store.ListSubscriptionsAsync(tenantId, context.RequestAborted);
            // SEC M-5: Never return HMAC secrets in GET responses
            var safeSubs = subs.Select(s => s with { HmacSecret = string.IsNullOrEmpty(s.HmacSecret) ? string.Empty : "[REDACTED]" });
            return Results.Ok(safeSubs);
        }).RequireAuthorization();

        app.MapPost("/api/v1/cdc/subscriptions", async (
            HttpContext context,
            Autheris.Domain.Model.CloudEventWebhookSubscription subscription,
            [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Events.Interfaces.ICloudEventSubscriptionStore store) =>
        {
            if (!IsAuthorizedSubscriptionAdmin(context.User))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var tenantId = EndpointSecurity.GetRequestTenant(context).Value;
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                tenantId = "default";
            }

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
                return Results.BadRequest(new { error = $"Target URL blocked by SSRF egress policy: {ex.Message}" });
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
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status429TooManyRequests);
            }

            var responseSub = securedSub with { HmacSecret = string.IsNullOrEmpty(securedSub.HmacSecret) ? string.Empty : "[REDACTED]" };
            return Results.Created($"/api/v1/cdc/subscriptions/{securedSub.Id}", responseSub);
        }).RequireAuthorization();

        app.MapDelete("/api/v1/cdc/subscriptions/{id}", async (
            string id,
            HttpContext context,
            [Microsoft.AspNetCore.Mvc.FromServices] Autheris.Application.Events.Interfaces.ICloudEventSubscriptionStore store) =>
        {
            if (!IsAuthorizedSubscriptionAdmin(context.User))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var tenantId = EndpointSecurity.GetRequestTenant(context).Value;
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                tenantId = "default";
            }
            var removed = await store.RemoveSubscriptionAsync(tenantId, id, context.RequestAborted);
            return removed ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization();

        return app;
    }

    private static bool IsAuthorizedSubscriptionAdmin(ClaimsPrincipal user)
        => user.IsInRole("GovernanceAdmin") ||
           user.IsInRole("StreamingAdmin") ||
           user.IsInRole("ClusterAdmin");

    /// <summary>
    /// SEC H-04: Cross-tenant CDC ingestion is decided by roles only. The former substring check ("ADMIN" in SID or
    /// user name) promoted accounts such as "CORP\badminton" to cluster admin and has been removed.
    /// </summary>
    internal static bool IsCdcClusterAdmin(ClaimsPrincipal user)
        => user.IsInRole("ClusterAdmin") || user.IsInRole("PlatformAdmin");

    internal static bool IsAuthorizedCdcIngestion(ClaimsPrincipal user)
        => IsCdcClusterAdmin(user) ||
           user.IsInRole("CdcIngestionService") ||
           user.IsInRole("StreamingAdmin") ||
           user.IsInRole("GovernanceAdmin");
}
