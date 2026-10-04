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

        return app;
    }

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
