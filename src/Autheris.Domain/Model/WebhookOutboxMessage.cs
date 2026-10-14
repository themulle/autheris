namespace Autheris.Domain.Model;

using System;

/// <summary>Lifecycle state of a webhook outbox row.</summary>
public enum WebhookOutboxState
{
    Pending = 0,
    InFlight = 1,
    Delivered = 2,
    Dead = 3
}

/// <summary>Which subscription id space a webhook outbox row refers to.</summary>
public enum WebhookSubscriptionSource
{
    /// <summary>A subscription from <c>Gateway:Webhooks:Subscriptions</c>.</summary>
    Configured = 0,

    /// <summary>A CloudEvents (CDC) subscription from the subscription store.</summary>
    CloudEvent = 1
}

/// <summary>
/// A durable webhook delivery. Rows never hold the target URL, the signing secret or custom headers; all three are
/// resolved from the subscription at delivery time (URLs may carry query tokens, and an admin changing a URL must
/// affect pending retries).
/// </summary>
/// <param name="OwnerTenantId">Subscription owner; <c>null</c> means a cluster-wide subscription.</param>
/// <param name="PayloadJson">The already masked/sanitized CloudEvent JSON body.</param>
public sealed record WebhookOutboxMessage(
    string Id,
    WebhookSubscriptionSource Source,
    string SubscriptionId,
    string? OwnerTenantId,
    string? EventTenantId,
    string EventId,
    string EventType,
    string EventSource,
    DateTimeOffset EventTime,
    string PayloadJson,
    int Attempt,
    int MaxAttempts,
    WebhookOutboxState State,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? LeaseUntil,
    string? LeaseOwner,
    int? LastStatusCode,
    string? LastErrorCode,
    DateTimeOffset CreatedAt);

/// <summary>Stable, machine-readable delivery error codes. Never free text (no resolved IPs, no exception messages).</summary>
public static class WebhookDeliveryErrorCodes
{
    public const string HttpStatus = "http_status";
    public const string HttpRedirect = "http_redirect";
    public const string Timeout = "timeout";
    public const string Network = "network";
    public const string Tls = "tls";
    public const string EgressDenied = "egress_denied";
    public const string DnsUnresolved = "dns_unresolved";
    public const string InvalidUrl = "invalid_url";
    public const string SecretMissing = "secret_missing";
    public const string SubscriptionNotFound = "subscription_not_found";
    public const string SubscriptionInactive = "subscription_inactive";
    public const string PayloadTooLarge = "payload_too_large";
    public const string MaxAttemptsExceeded = "max_attempts_exceeded";
}

/// <summary>Owner scope for dead-letter listings. There is intentionally no "all tenants" scope.</summary>
public readonly record struct WebhookOwnerScope(string? TenantId, bool ClusterScope);

/// <summary>Dead-letter projection without payload.</summary>
public sealed record WebhookDeadLetter(
    string Id,
    string SubscriptionId,
    string EventId,
    string EventType,
    int Attempt,
    int? LastStatusCode,
    string? LastErrorCode,
    DateTimeOffset CreatedAt);
