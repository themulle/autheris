namespace Autheris.Api.Endpoints;

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// PLAN-CDC-99 D-A14 / SEC-CDC99-21: administration of the governed CDC streams. Global governance administrators only; never returns
/// payloads, fingerprints, wrapped keys or key material (SEC-CDC99-11). Unknown and malformed ids answer identically (404).
/// </summary>
public static class CdcStreamAdminEndpoints
{
    public static IEndpointRouteBuilder MapCdcStreamAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/cdc/streams", (HttpContext context, CdcStreamAdminService service, CancellationToken ct) => ListAsync(context, service, ct))
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapPost("/api/v1/cdc/streams/{id}/resync", (
                string id, bool? discardUnreadable, string? confirmStreamId, HttpContext context, CdcStreamAdminService service, IAuditWriter audit, CancellationToken ct)
            => ResyncAsync(id, discardUnreadable ?? false, confirmStreamId, context, service, audit, ct))
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
            .WithAudit(AuditLevel.Full, AuditEventTypes.CdcStreamResync);

        app.MapPost("/api/v1/cdc/streams/{id}/rotate-image-key", (
                string id, HttpContext context, CdcStreamAdminService service, IAuditWriter audit, CancellationToken ct)
            => RotateImageKeyAsync(id, context, service, audit, ct))
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
            .WithAudit(AuditLevel.Full, AuditEventTypes.CdcStreamKeyRotated);

        app.MapPost("/api/v1/cdc/streams/{id}/reset-watermark", (
                string id, ResetWatermarkRequest request, HttpContext context, CdcStreamAdminService service, IAuditWriter audit, CancellationToken ct)
            => ResetWatermarkAsync(id, request, context, service, audit, ct))
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
            .WithAudit(AuditLevel.Full, AuditEventTypes.CdcStreamWatermarkReset);

        return app;
    }

    internal static async Task<IResult> ListAsync(HttpContext context, CdcStreamAdminService service, CancellationToken ct)
    {
        if (!EndpointSecurity.IsGlobalGovernanceAdmin(context.User))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var listing = await service.ListAsync(ct).ConfigureAwait(false);
        return Results.Ok(new { streams = listing.Streams, keyCountsByKek = listing.WrappedKeysByKek });
    }

    internal static async Task<IResult> ResyncAsync(
        string id, bool discardUnreadable, string? confirmStreamId, HttpContext context, CdcStreamAdminService service, IAuditWriter audit, CancellationToken ct)
    {
        if (!EndpointSecurity.IsGlobalGovernanceAdmin(context.User))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!CdcStreamId.TryParse(id, out var streamId))
        {
            return Results.NotFound();
        }

        // SEC-CDC99-21: the destructive variant needs an explicit confirmation naming the stream.
        if (discardUnreadable && !string.Equals(confirmStreamId, streamId.Value, StringComparison.Ordinal))
        {
            return Results.BadRequest(new { error = "discardUnreadable requires confirmStreamId equal to the stream id." });
        }

        var outcome = await service.ResyncAsync(streamId, discardUnreadable, ct).ConfigureAwait(false);
        if (outcome.Status == CdcResyncStatus.NotFound)
        {
            return Results.NotFound();
        }

        // The resync (and a discard) is already committed: the audit write never takes the request token.
        if (outcome.Status is CdcResyncStatus.Done or CdcResyncStatus.KeyUnavailable || outcome.DiscardedEvents > 0)
        {
            var keyRefused = outcome.Status == CdcResyncStatus.KeyUnavailable;
            await RecordAsync(context, audit, AuditEventTypes.CdcStreamResync, keyRefused
                ? (object)new
                {
                    streamId = streamId.Value,
                    discardUnreadable,
                    discardedEvents = 0,
                    restarted = false,
                    refused = CdcActivationCodes.KeyUnavailable
                }
                : new
                {
                    streamId = streamId.Value,
                    discardUnreadable,
                    discardedEvents = outcome.DiscardedEvents,
                    restarted = outcome.Status == CdcResyncStatus.Done
                }).ConfigureAwait(false);
        }

        return outcome.Status switch
        {
            CdcResyncStatus.Done => Results.Ok(new { streamId = streamId.Value, discardedEvents = outcome.DiscardedEvents }),
            CdcResyncStatus.KeyUnavailable => Results.Json(new { error = CdcActivationCodes.KeyUnavailable }, statusCode: StatusCodes.Status503ServiceUnavailable),
            CdcResyncStatus.Unavailable => Results.Json(
                new { error = "The stream could not be restarted now; retry later.", discardedEvents = outcome.DiscardedEvents }, statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => throw new InvalidOperationException("Unhandled CDC resync status.")
        };
    }

    internal static async Task<IResult> RotateImageKeyAsync(
        string id, HttpContext context, CdcStreamAdminService service, IAuditWriter audit, CancellationToken ct)
    {
        if (!EndpointSecurity.IsGlobalGovernanceAdmin(context.User))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!CdcStreamId.TryParse(id, out var streamId) || !await service.RotateImageKeyAsync(streamId, ct).ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        await RecordAsync(context, audit, AuditEventTypes.CdcStreamKeyRotated, new { streamId = streamId.Value, purpose = "image" }).ConfigureAwait(false);
        return Results.Ok(new { streamId = streamId.Value });
    }

    internal static async Task<IResult> ResetWatermarkAsync(
        string id, ResetWatermarkRequest request, HttpContext context, CdcStreamAdminService service, IAuditWriter audit, CancellationToken ct)
    {
        if (!EndpointSecurity.IsGlobalGovernanceAdmin(context.User))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!CdcStreamId.TryParse(id, out var streamId))
        {
            return Results.NotFound();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Justification) || request.Dimensions is null)
        {
            return Results.BadRequest(new { error = "Justification and Dimensions are required." });
        }

        if (!await service.ResetWatermarkAsync(streamId, request.Dimensions, request.Justification, ct).ConfigureAwait(false))
        {
            return Results.NotFound();
        }

        await RecordAsync(context, audit, AuditEventTypes.CdcStreamWatermarkReset, new
        {
            streamId = streamId.Value,
            justification = request.Justification,
            dimensions = request.Dimensions
        }).ConfigureAwait(false);

        return Results.Ok(new { streamId = streamId.Value, status = "reset" });
    }

    private static async Task RecordAsync(HttpContext context, IAuditWriter audit, string eventType, object details)
    {
        TenantId tenant;
        try
        {
            tenant = context.User.GetTenantId();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or Autheris.Domain.Exceptions.GatewaySecurityException)
        {
            tenant = default;
        }

        await audit.RecordAsync(new AuditLogEntry
        {
            TenantId = tenant,
            EventType = eventType,
            ActorSid = context.User.GetUserSid() ?? new Sid("S-1-5-21-UNKNOWN"),
            TargetTable = string.Empty,
            Decision = "ALLOW",
            TraceId = context.TraceIdentifier,
            DetailsJson = JsonSerializer.Serialize(details)
        }, CancellationToken.None).ConfigureAwait(false);
        context.Features.Get<AuditContext>()?.MarkHandled();
    }
}

public sealed record ResetWatermarkRequest(IReadOnlyDictionary<string, string> Dimensions, string Justification);

