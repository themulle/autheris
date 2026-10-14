namespace Autheris.Application.Webhooks;

using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security;
using Autheris.Domain.Events;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Facade of the webhook pipeline (WP-ARC-A-02): matches an event against the configured subscriptions, builds the signed-at-delivery
/// CloudEvent body (sanitized) and durably enqueues one outbox row per matching subscription. Delivery, retries and dead letters are
/// handled by <see cref="WebhookDeliveryHostedService"/>, so <see cref="DispatchEventAsync"/> completes after the enqueue, not after any HTTP call.
/// </summary>
public sealed class WebhookDispatcherService : IWebhookDispatcherService
{
    private const string EventSource = "https://autheris.gateway/events";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IWebhookOutbox _outbox;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<WebhookDispatcherService> _logger;
    private readonly IHostEnvironment? _environment;
    private readonly TimeProvider _timeProvider;

    public WebhookDispatcherService(
        IWebhookOutbox outbox,
        IOptions<GatewayOptions> options,
        ILogger<WebhookDispatcherService> logger,
        IHostEnvironment? environment = null,
        TimeProvider? timeProvider = null)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _environment = environment;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task DispatchEventAsync(IGatewayEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var webhooks = _options.Value.Webhooks;
        if (!webhooks.Enabled || webhooks.Subscriptions.Count == 0)
        {
            return;
        }

        var matching = webhooks.Subscriptions.FindAll(s => s.MatchesEvent(@event.EventType, @event.TenantId));
        if (matching.Count == 0)
        {
            return;
        }

        var cloudEvent = new GatewayCloudEvent(
            Id: @event.EventId,
            Source: EventSource,
            Type: @event.EventType,
            Time: @event.Timestamp,
            Subject: @event.CorrelationId,
            DataContentType: "application/json",
            Data: EventRedaction.Sanitize(@event, JsonOptions), // SR-L-15 safety net
            TenantId: @event.TenantId,
            CorrelationId: @event.CorrelationId);

        var payload = JsonSerializer.Serialize(cloudEvent, JsonOptions);
        if (Encoding.UTF8.GetByteCount(payload) > webhooks.EffectiveMaxPayloadBytes)
        {
            _logger.LogWarning(
                "Webhook payload of event {EventType} ({EventId}) exceeds {MaxBytes} bytes ({ErrorCode}); not enqueued.",
                @event.EventType, @event.EventId, webhooks.EffectiveMaxPayloadBytes, WebhookDeliveryErrorCodes.PayloadTooLarge);
            return;
        }

        var requireSecret = webhooks.RequireSecret ?? !(_environment?.IsDevelopment() ?? false);
        var now = _timeProvider.GetUtcNow();
        foreach (var subscription in matching)
        {
            if (string.IsNullOrWhiteSpace(subscription.Id))
            {
                _logger.LogError("A configured webhook subscription has no Id; delivery skipped (validated at startup).");
                continue;
            }

            // SR-L-13: fail closed, never queue a delivery that could only go out unsigned.
            if (requireSecret && string.IsNullOrEmpty(subscription.Secret))
            {
                _logger.LogWarning(
                    "Webhook subscription {SubscriptionId} has no secret; delivery to {TargetUrl} dropped (Gateway:Webhooks:RequireSecret).",
                    subscription.Id, UrlRedactor.Redact(subscription.TargetUrl));
                continue;
            }

            var message = new WebhookOutboxMessage(
                Id: Guid.CreateVersion7().ToString("N"),
                Source: WebhookSubscriptionSource.Configured,
                SubscriptionId: subscription.Id,
                OwnerTenantId: string.IsNullOrWhiteSpace(subscription.TenantId) ? null : subscription.TenantId,
                EventTenantId: @event.TenantId,
                EventId: @event.EventId,
                EventType: @event.EventType,
                EventSource: EventSource,
                EventTime: @event.Timestamp,
                PayloadJson: payload,
                Attempt: 0,
                MaxAttempts: subscription.EffectiveMaxAttempts,
                State: WebhookOutboxState.Pending,
                NextAttemptAt: now,
                LeaseUntil: null,
                LeaseOwner: null,
                LastStatusCode: null,
                LastErrorCode: null,
                CreatedAt: now);

            try
            {
                await _outbox.EnqueueAsync(message, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One failing enqueue must not block the other subscriptions; storage messages may carry connection details.
                _logger.LogError(
                    "Webhook enqueue for subscription {SubscriptionId} and event {EventId} failed ({ErrorType}).",
                    subscription.Id, @event.EventId, ex.GetType().Name);
            }
        }
    }
}
