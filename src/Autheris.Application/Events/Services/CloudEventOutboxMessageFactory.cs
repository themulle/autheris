namespace Autheris.Application.Events.Services;

using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// Builds the durable webhook outbox row for one CloudEvent and one subscription (WP-ARC-A-02). The row carries the (already masked)
/// envelope JSON only: target URL, secret and custom headers are resolved from the subscription at delivery time.
/// </summary>
public static class CloudEventOutboxMessageFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public const int DefaultMaxPayloadBytes = 262144;
    public const int MaxSubscriptionIdLength = 128;
    public const int MaxEventIdLength = 128;
    public const int MaxEventTypeLength = 256;
    public const int MaxEventSourceLength = 512;

    private static void Bound(string? value, int max, string field)
    {
        if (value is { } v && v.Length > max)
        {
            throw new WebhookOutboxBoundsException(field);
        }
    }

    public static WebhookOutboxMessage Create(
        CloudEventWebhookSubscription subscription,
        CloudEventEnvelope envelope,
        DateTimeOffset now,
        int maxAttempts,
        int maxPayloadBytes = DefaultMaxPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(envelope);

        // Column bounds of the outbox table; a violation drops the event instead of truncating or failing the insert.
        Bound(subscription.Id, MaxSubscriptionIdLength, "subscription_id");
        Bound(envelope.Id, MaxEventIdLength, "event_id");
        Bound(envelope.Type, MaxEventTypeLength, "event_type");
        Bound(envelope.Source, MaxEventSourceLength, "event_source");

        var payload = JsonSerializer.Serialize(envelope, JsonOptions);
        if (Encoding.UTF8.GetByteCount(payload) > maxPayloadBytes)
        {
            throw new WebhookOutboxBoundsException("payload");
        }

        return new WebhookOutboxMessage(
            Id: Guid.CreateVersion7().ToString("N"),
            Source: WebhookSubscriptionSource.CloudEvent,
            SubscriptionId: subscription.Id,
            OwnerTenantId: subscription.TenantId,
            EventTenantId: envelope.TenantId,
            EventId: envelope.Id,
            EventType: envelope.Type,
            EventSource: envelope.Source,
            EventTime: envelope.Time,
            PayloadJson: payload,
            Attempt: 0,
            MaxAttempts: Math.Clamp(maxAttempts, 1, WebhookSubscription.MaxAllowedAttempts),
            State: WebhookOutboxState.Pending,
            NextAttemptAt: now,
            LeaseUntil: null,
            LeaseOwner: null,
            LastStatusCode: null,
            LastErrorCode: null,
            CreatedAt: now);
    }
}
