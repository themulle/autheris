namespace Autheris.Application.Events.Services;

using System;
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

    public static WebhookOutboxMessage Create(
        CloudEventWebhookSubscription subscription,
        CloudEventEnvelope envelope,
        DateTimeOffset now,
        int maxAttempts)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(envelope);

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
            PayloadJson: JsonSerializer.Serialize(envelope, JsonOptions),
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
