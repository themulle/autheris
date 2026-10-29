namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Endpoints.Support;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Events.Services;
using Autheris.Application.Webhooks;
using Autheris.Application.Interfaces;
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
using Microsoft.Extensions.Hosting;
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

                // SR-L-20: the row tenant is read from the registered tenant column of the target table.
                var cdcEvent = DebeziumCdcParser.Parse(body);
                var services = request.HttpContext.RequestServices;
                var metadataRepository = services.GetService<ITableMetadataRepository>();
                var metadata = metadataRepository == null
                    ? null
                    : await metadataRepository.GetTableMetadataAsync(cdcEvent.Table, request.HttpContext.RequestAborted);
                var tenantColumn = metadata?.TenantColumnName;
                if (tenantColumn != null)
                {
                    cdcEvent = DebeziumCdcParser.Parse(body, null, tenantColumn);
                }

                var requireTenantColumn = services.GetService<IOptions<GatewayOptions>>()?.Value.DataSources.RequireTenantColumn == true;
                var rejection = ApplyIngestTenantPolicy(cdcEvent, isClusterAdmin, callerTenant, tenantColumn != null, requireTenantColumn, out cdcEvent);
                if (rejection != null)
                {
                    return rejection;
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

        // F-EVT-01: CloudEvents Outbound Webhook Subscriptions (answer 501 when Gateway:CdcWebhooks:Enabled is false;
        // the subscription store and the delivery service are not registered while disabled).
        app.MapGet("/api/v1/cdc/subscriptions", (Delegate)ListSubscriptionsAsync)
            .RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        app.MapPost("/api/v1/cdc/subscriptions", CreateSubscriptionAsync)
            .RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        app.MapDelete("/api/v1/cdc/subscriptions/{id}", DeleteSubscriptionAsync)
            .RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        return app;
    }

    /// <summary>
    /// A-1 / SEC-4 / SR-L-20: fail-closed tenant policy for ingested CDC events. Returns a rejection result or <c>null</c>;
    /// <paramref name="resolved"/> is the (possibly tenant-stamped) event.
    /// </summary>
    internal static IResult? ApplyIngestTenantPolicy(
        CdcEvent cdcEvent,
        bool isClusterAdmin,
        string? callerTenant,
        bool tableHasTenantColumn,
        bool requireTenantColumn,
        out CdcEvent resolved)
    {
        resolved = cdcEvent;

        // A-1: even canonical cluster admins must name the target tenant explicitly (no implicit/null tenant).
        if (isClusterAdmin && string.IsNullOrWhiteSpace(cdcEvent.TenantId))
        {
            return Results.BadRequest(new { error = "CDC event must carry an explicit tenant" });
        }

        if (isClusterAdmin)
        {
            return null;
        }

        // SEC-4: Enforce strict fail-closed tenant isolation on ingested CDC events
        if (string.IsNullOrWhiteSpace(callerTenant))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!string.IsNullOrWhiteSpace(cdcEvent.TenantId))
        {
            return string.Equals(cdcEvent.TenantId, callerTenant, StringComparison.Ordinal)
                ? null
                : Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // No tenant value on the row: never stamp the caller tenant onto a tenant-scoped table.
        if (tableHasTenantColumn)
        {
            return Results.BadRequest(new { error = "CDC row must carry a tenant value" });
        }

        if (requireTenantColumn)
        {
            return Results.BadRequest(new { error = "CDC target table has no tenant column" });
        }

        resolved = cdcEvent with { TenantId = callerTenant };
        return null;
    }

    /// <summary>Returns a 501 problem result while the CDC webhook feature flag is off; otherwise <c>null</c>.</summary>
    internal static IResult? GuardCdcWebhooksDisabled(HttpContext context)
    {
        // The registration-time flag is the single source of truth (it decides whether the store and delivery service exist).
        // Changing the configuration later has no effect until a restart. Without a registered flag (unit hosts) the options decide.
        var flag = context.RequestServices.GetService<CdcWebhooksFeatureFlag>();
        var enabled = flag is not null
            ? flag.Enabled
            : context.RequestServices.GetService<IOptions<CdcWebhooksOptions>>()?.Value is { Enabled: true };
        if (enabled)
        {
            return null;
        }

        return Results.Problem(
            title: "CDC webhook subscriptions are not enabled",
            detail: "Outbound CDC webhook delivery is disabled. Set Gateway:CdcWebhooks:Enabled to true to enable it.",
            statusCode: StatusCodes.Status501NotImplemented);
    }

    // DT-A-9: the request tenant is the resolved tenant; an unresolved request has none (GetRequestTenant throws) and is never mapped to a literal.
    private static string ResolveTenant(HttpContext context) => EndpointSecurity.GetRequestTenant(context).Value;

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
        // SEC M-5: never return HMAC secrets or the creator's group list in GET responses.
        return Results.Ok(subs.Select(CdcSubscriptionView.From));
    }

    private static readonly System.Text.RegularExpressions.Regex TableFilterPattern =
        new(@"^(\*|[\w$#\-]+(\.[\w$#\-]+){0,2})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking);

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

        var services = context.RequestServices;
        var store = services.GetRequiredService<ICloudEventSubscriptionStore>();
        var options = services.GetRequiredService<IOptions<CdcWebhooksOptions>>().Value;
        var tenantId = ResolveTenant(context);
        // Fail closed: without a host environment the deployment is treated as production.
        var isDev = services.GetService<IHostEnvironment>()?.IsDevelopment() == true;

        if (subscription is null || string.IsNullOrWhiteSpace(subscription.TargetUrl) || !Uri.TryCreate(subscription.TargetUrl, UriKind.Absolute, out var targetUri))
        {
            return Results.BadRequest(new { error = "Invalid absolute target URL" });
        }

        if (subscription.TargetUrl.Length > 2048
            || string.IsNullOrEmpty(subscription.FilterTable)
            || subscription.FilterTable.Length > 512
            || !TableFilterPattern.IsMatch(subscription.FilterTable)
            || (subscription.FilterOperations ?? []).Any(o => !Enum.IsDefined(o)))
        {
            return SafeErrorResults.BadRequest("CDC_FILTER_INVALID", "The table or operation filter is invalid.", context);
        }

        try
        {
            await Autheris.Application.Security.EgressUrlPolicy.ValidateResolvedAsync(targetUri, isDev: isDev, ct: context.RequestAborted);
        }
        catch (Exception ex)
        {
            SafeErrorResults.LogFailure(context, ex, "CDC_TARGET_URL_NOT_ALLOWED");
            return SafeErrorResults.BadRequest("CDC_TARGET_URL_NOT_ALLOWED", "The target URL is not allowed.", context);
        }

        if (!CdcCustomHeaders.WithinBounds(subscription.CustomHeaders)
            || (subscription.CustomHeaders is { } headers
            && headers.Any(h => Autheris.Application.Webhooks.WebhookDeliveryClient.IsForbiddenCustomHeader(h.Key)
                                || h.Value is null || h.Value.Contains('\r') || h.Value.Contains('\n') || h.Key.Contains('\r') || h.Key.Contains('\n'))))
        {
            return SafeErrorResults.BadRequest("CDC_CUSTOM_HEADER_NOT_ALLOWED", "A custom header is not allowed.", context);
        }

        // SEC M-5: Server-side ID generation; client-chosen ID is ignored to prevent overwrite attacks
        var serverId = Guid.NewGuid().ToString("N");

        // Secret: required (32..512 chars) outside Development, stored only KEK-protected; never persist plaintext (fail closed).
        // Custom header values (tokens) are protected the same way.
        var secret = subscription.HmacSecret ?? string.Empty;
        var protectedSecret = string.Empty;
        var hasHeaders = subscription.CustomHeaders is { Count: > 0 };
        if (secret.Length == 0 && !isDev)
        {
            return SafeErrorResults.BadRequest("CDC_SECRET_REQUIRED", "A signing secret of 32 to 512 characters is required.", context);
        }

        if (secret.Length > 512 || (secret.Length > 0 && !isDev && secret.Length < 32))
        {
            return SafeErrorResults.BadRequest("CDC_SECRET_REQUIRED", "A signing secret of 32 to 512 characters is required.", context);
        }

        IReadOnlyDictionary<string, string>? protectedHeaders = null;
        if (secret.Length > 0 || hasHeaders)
        {
            var protector = services.GetService<IWebhookSecretProtector>();
            if (protector is null || !protector.IsAvailable)
            {
                return SafeErrorResults.Problem(
                    StatusCodes.Status503ServiceUnavailable, "CDC_SECRET_STORE_UNAVAILABLE", "The secret store is not available.", context);
            }

            try
            {
                if (secret.Length > 0)
                {
                    protectedSecret = protector.Protect(tenantId, serverId, secret);
                }

                if (hasHeaders)
                {
                    protectedHeaders = new Dictionary<string, string>
                    {
                        [CdcCustomHeaders.ProtectedKey] = protector.Protect(tenantId, serverId, System.Text.Json.JsonSerializer.Serialize(subscription.CustomHeaders))
                    };
                }
            }
            catch (WebhookSecretUnavailableException ex)
            {
                SafeErrorResults.LogFailure(context, ex, "CDC_SECRET_STORE_UNAVAILABLE");
                return SafeErrorResults.Problem(
                    StatusCodes.Status503ServiceUnavailable, "CDC_SECRET_STORE_UNAVAILABLE", "The secret store is not available.", context);
            }
        }

        // Creator snapshot: authorisation of every delivered event is evaluated as this principal. Never taken from the request body.
        var capture = CdcSubscriptionPrincipal.Capture(context.User, options);
        if (capture.Error == CdcSnapshotCaptureError.MissingSid)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!capture.IsSuccess)
        {
            return SafeErrorResults.BadRequest("CDC_SNAPSHOT_TOO_LARGE", "The creator principal has too many group memberships.", context);
        }

        var time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var securedSub = subscription with
        {
            Id = serverId,
            TenantId = tenantId,
            IsEnabled = true,
            HmacSecret = protectedSecret,
            CustomHeaders = protectedHeaders,
            FilterOperations = subscription.FilterOperations ?? [],
            CreatedBySid = capture.Sid,
            CreatedByGroupSids = capture.GroupSids,
            CreatedByRoles = capture.Roles,
            CreatedAt = time.GetUtcNow()
        };
        try
        {
            await store.RegisterSubscriptionAsync(securedSub, options.MaxSubscriptionsPerTenant, context.RequestAborted);
        }
        catch (CloudEventSubscriptionStoreBusyException ex)
        {
            SafeErrorResults.LogFailure(context, ex, "CDC_STORE_BUSY");
            return SafeErrorResults.Problem(StatusCodes.Status503ServiceUnavailable, "CDC_STORE_BUSY", "The subscription store is busy; retry.", context);
        }
        catch (InvalidOperationException ex)
        {
            SafeErrorResults.LogFailure(context, ex, "CDC_SUBSCRIPTION_QUOTA_EXCEEDED");
            return SafeErrorResults.TooManyRequests("CDC_SUBSCRIPTION_QUOTA_EXCEEDED", "The subscription limit has been reached.", context);
        }

        return Results.Created($"/api/v1/cdc/subscriptions/{securedSub.Id}", CdcSubscriptionView.From(securedSub));
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

/// <summary>Redacted subscription representation: no secret (not even the ciphertext) and no creator group list.</summary>
internal sealed record CdcSubscriptionView(
    string Id,
    string TenantId,
    string TargetUrl,
    string FilterTable,
    IReadOnlyList<CdcOperation> FilterOperations,
    string HmacSecret,
    bool IsEnabled,
    bool HasCustomHeaders,
    string? CreatedBySid,
    int CreatedByRoleCount,
    DateTimeOffset? CreatedAt)
{
    public static CdcSubscriptionView From(CloudEventWebhookSubscription s) => new(
        s.Id, s.TenantId, s.TargetUrl, s.FilterTable, s.FilterOperations,
        string.IsNullOrEmpty(s.HmacSecret) ? string.Empty : "[REDACTED]",
        s.IsEnabled, s.CustomHeaders is { Count: > 0 }, s.CreatedBySid, s.CreatedByRoles?.Count ?? 0, s.CreatedAt);
}
