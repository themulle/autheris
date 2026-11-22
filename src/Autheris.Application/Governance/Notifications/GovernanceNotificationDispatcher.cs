using System;
using System.Diagnostics;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Governance.Notifications;

/// <summary>
/// Dispatches Data Governance notifications over HTTPS through an SSRF-protected outbound HTTP client without exposing
/// destination URLs or user secrets in logs, audit payloads, or exceptions.
/// </summary>
public sealed class GovernanceNotificationDispatcher : IGovernanceNotificationDispatcher
{
    private const int MaxDeliveryAttempts = 3;

    private readonly IOptions<GovernanceNotificationOptions> _options;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IAuditWriter? _auditWriter;
    private readonly IAccessPolicySettingsProvider? _policyProvider;
    private readonly INotificationWebhookUrlProtector? _urlProtector;
    private readonly AdaptiveCardPayloadFormatter _formatter;
    private readonly ILogger<GovernanceNotificationDispatcher>? _logger;
    private readonly TimeProvider _time;

    public GovernanceNotificationDispatcher(
        IOptions<GovernanceNotificationOptions>? options = null,
        IHttpClientFactory? httpClientFactory = null,
        IAuditWriter? auditWriter = null,
        IAccessPolicySettingsProvider? policyProvider = null,
        INotificationWebhookUrlProtector? urlProtector = null,
        AdaptiveCardPayloadFormatter? formatter = null,
        ILogger<GovernanceNotificationDispatcher>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? Options.Create(new GovernanceNotificationOptions());
        _httpClientFactory = httpClientFactory;
        _auditWriter = auditWriter;
        _policyProvider = policyProvider;
        _urlProtector = urlProtector;
        _formatter = formatter ?? new AdaptiveCardPayloadFormatter();
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public Task DispatchAsync(GovernanceNotificationMessage message, CancellationToken ct = default) =>
        TryDispatchAsync(message, ct);

    public async Task<bool> TryDispatchAsync(GovernanceNotificationMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var targetUrl = await ResolveTargetUrlAsync(message.TenantId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            return true;
        }

        if (!NotificationWebhookUrlUtility.TryValidateHttpsUrl(targetUrl, out var targetUri, out _))
        {
            _logger?.LogWarning(
                "Governance notification webhook skipped for event {EventType} in tenant {TenantId}: target URL rejected (HTTPS required).",
                message.EventType,
                message.TenantId);
            await RecordFinalFailureAuditAsync(message, hostHash: null, statusCode: null, "INVALID_SCHEME_OR_URL", attempts: 0, ct).ConfigureAwait(false);
            return false;
        }

        if (_httpClientFactory is null)
        {
            _logger?.LogWarning(
                "Governance notification HTTP client is unavailable for event {EventType} in tenant {TenantId}.",
                message.EventType,
                message.TenantId);
            return false;
        }

        var hostHash = NotificationWebhookUrlUtility.ComputeHostHash(targetUri);
        var settings = _options.Value;
        var payloadJson = _formatter.Format(message, settings.Webhook.Format);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 60));

        int? lastStatusCode = null;
        var errorCode = "DELIVERY_FAILED";
        var attemptsMade = 0;

        for (var attempt = 1; attempt <= MaxDeliveryAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            attemptsMade = attempt;

            try
            {
                var client = _httpClientFactory.CreateClient(EgressIntegrations.GovernanceNotifications);
                using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reqCts.CancelAfter(timeout);

                using var request = new HttpRequestMessage(HttpMethod.Post, targetUri)
                {
                    Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
                };

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, reqCts.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                lastStatusCode = status;

                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                if (status is >= 300 and < 400)
                {
                    errorCode = WebhookDeliveryErrorCodes.HttpRedirect;
                    break;
                }

                if (status is >= 400 and < 500 && status != 408 && status != 429)
                {
                    errorCode = WebhookDeliveryErrorCodes.HttpStatus;
                    break;
                }

                errorCode = WebhookDeliveryErrorCodes.HttpStatus;
            }
            catch (SecurityException)
            {
                errorCode = WebhookDeliveryErrorCodes.EgressDenied;
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                errorCode = WebhookDeliveryErrorCodes.Timeout;
            }
            catch (HttpRequestException)
            {
                errorCode = WebhookDeliveryErrorCodes.Network;
            }

            if (attempt < MaxDeliveryAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct).ConfigureAwait(false);
            }
        }

        _logger?.LogWarning(
            "Governance notification delivery failed for event {EventType} in tenant {TenantId} ({ErrorCode}, HTTP {StatusCode}) after {Attempts} attempt(s).",
            message.EventType,
            message.TenantId,
            errorCode,
            lastStatusCode,
            attemptsMade);

        await RecordFinalFailureAuditAsync(message, hostHash, lastStatusCode, errorCode, attemptsMade, ct).ConfigureAwait(false);
        return false;
    }

    public async Task AuditStartupChannelStateAsync(CancellationToken ct = default)
    {
        if (_auditWriter is null)
        {
            return;
        }

        var url = _options.Value.Webhook.Url;
        var hasUrl = !string.IsNullOrWhiteSpace(url);
        Uri? uri = null;
        var valid = hasUrl && NotificationWebhookUrlUtility.TryValidateHttpsUrl(url, out uri, out _);
        var hostHash = valid && uri is not null ? NotificationWebhookUrlUtility.ComputeHostHash(uri) : null;
        var stateLabel = valid ? "CONFIGURED" : hasUrl ? "INVALID" : "UNCONFIGURED";

        var detailsJson = JsonSerializer.Serialize(new
        {
            channel = "governance_notifications",
            configured = valid,
            hostHash,
            source = "startup"
        });

        await _auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = _time.GetUtcNow().ToUniversalTime(),
            EventType = AuditEventTypes.NotificationChannelChanged,
            ActorSid = new Sid("SYSTEM:STARTUP"),
            TargetTable = "governance_notifications",
            Decision = stateLabel,
            TraceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
            DetailsJson = detailsJson,
            TenantId = TenantId.Platform
        }, ct).ConfigureAwait(false);
    }

    private async Task<string?> ResolveTargetUrlAsync(string tenantIdRaw, CancellationToken ct)
    {
        if (_policyProvider is not null && _urlProtector is not null && TenantId.TryParse(tenantIdRaw, out var tenantId))
        {
            try
            {
                var policy = await _policyProvider.GetEffectiveAsync(tenantId, ct).ConfigureAwait(false);
                if (policy.NotificationWebhookConfigured && !string.IsNullOrWhiteSpace(policy.NotificationWebhookUrlProtected))
                {
                    return _urlProtector.Unprotect(policy.NotificationWebhookUrlProtected);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(
                    "Unable to unprotect tenant notification webhook configuration for tenant {TenantId} ({ErrorType}).",
                    tenantIdRaw,
                    ex.GetType().Name);
                return null;
            }
        }

        return _options.Value.Webhook.Url;
    }

    private async Task RecordFinalFailureAuditAsync(
        GovernanceNotificationMessage message,
        string? hostHash,
        int? statusCode,
        string errorCode,
        int attempts,
        CancellationToken ct)
    {
        if (_auditWriter is null)
        {
            return;
        }

        var tenant = TenantId.TryParse(message.TenantId, out var validTenant) ? validTenant : TenantId.Platform;
        var detailsJson = JsonSerializer.Serialize(new
        {
            channel = "governance_notifications",
            eventType = message.EventType,
            hostHash,
            statusCode,
            errorCode,
            attempts
        });

        await _auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = _time.GetUtcNow().ToUniversalTime(),
            EventType = AuditEventTypes.WebhookDeliveryFailed,
            ActorSid = new Sid("SYSTEM:NOTIFICATIONS"),
            TargetTable = "governance_notifications",
            Decision = "FAILED",
            TraceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
            DetailsJson = detailsJson,
            TenantId = tenant
        }, ct).ConfigureAwait(false);
    }
}
