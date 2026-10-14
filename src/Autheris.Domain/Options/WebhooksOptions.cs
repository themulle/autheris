namespace Autheris.Domain.Options;

using System;
using System.Collections.Generic;

public sealed class WebhooksOptions
{
    public bool Enabled { get; init; } = true;
    public int DefaultTimeoutSeconds { get; init; } = 5;
    public List<WebhookSubscription> Subscriptions { get; init; } = [];

    /// <summary>
    /// SR-L-13: when a subscription has no secret, drop the delivery instead of sending it unsigned.
    /// <c>null</c> (default) means required outside Development.
    /// </summary>
    public bool? RequireSecret { get; init; }

    /// <summary>Parallel delivery loops per node (clamped 1..32).</summary>
    public int DeliveryWorkers { get; init; } = 4;

    /// <summary>Rows claimed per poll by one worker (clamped 1..100).</summary>
    public int DeliveryBatchSize { get; init; } = 8;

    /// <summary>Outbox poll interval in seconds.</summary>
    public int PollIntervalSeconds { get; init; } = 2;

    /// <summary>Base of the exponential retry backoff in seconds.</summary>
    public int RetryBaseSeconds { get; init; } = 5;

    /// <summary>Upper bound of a single retry delay in seconds.</summary>
    public int RetryMaxDelaySeconds { get; init; } = 3600;

    /// <summary>Upper bound honoured for a <c>Retry-After</c> response header, in seconds.</summary>
    public int MaxRetryAfterSeconds { get; init; } = 3600;

    /// <summary>
    /// Consecutive failures of one subscription (per node) after which its pending rows are paused for
    /// <see cref="CircuitCooldownSeconds"/>. This is a pause, not a deactivation.
    /// </summary>
    public int AutoDisableAfterConsecutiveFailures { get; init; } = 20;

    /// <summary>Pause duration applied when the circuit opens, in seconds.</summary>
    public int CircuitCooldownSeconds { get; init; } = 300;

    /// <summary>Maximum serialized CloudEvent size accepted into the outbox.</summary>
    public int MaxPayloadBytes { get; init; } = 262144;

    /// <summary>Retention of delivered rows (also the dedupe window).</summary>
    public int DeliveredRetentionHours { get; init; } = 24;

    /// <summary>Retention of dead-lettered rows.</summary>
    public int DeadLetterRetentionDays { get; init; } = 14;

    public int EffectiveDeliveryWorkers => Math.Clamp(DeliveryWorkers, 1, 32);

    public int EffectiveBatchSize => Math.Clamp(DeliveryBatchSize, 1, 100);

    public int EffectivePollIntervalSeconds => Math.Max(1, PollIntervalSeconds);

    public int EffectiveRetryBaseSeconds => Math.Max(1, RetryBaseSeconds);

    public int EffectiveRetryMaxDelaySeconds => Math.Max(EffectiveRetryBaseSeconds, RetryMaxDelaySeconds);

    public int EffectiveMaxRetryAfterSeconds => Math.Max(0, MaxRetryAfterSeconds);

    public int EffectiveCircuitThreshold => Math.Max(1, AutoDisableAfterConsecutiveFailures);

    public int EffectiveCircuitCooldownSeconds => Math.Max(1, CircuitCooldownSeconds);

    public int EffectiveMaxPayloadBytes => Math.Max(1024, MaxPayloadBytes);

    /// <summary>Lease long enough for one claimed batch to be delivered sequentially at full timeout.</summary>
    public TimeSpan EffectiveLease =>
        TimeSpan.FromSeconds((EffectiveBatchSize * (Math.Max(1, DefaultTimeoutSeconds) + 2)) + 30);
}

public sealed class WebhookSubscription
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string TargetUrl { get; init; } = string.Empty;
    public List<string> EventFilters { get; init; } = [];
    public string Secret { get; init; } = string.Empty;
    public string? TenantId { get; init; }

    /// <summary>
    /// SR-M-12: explicit opt-in for cluster-scope subscriptions (configured by a cluster administrator).
    /// A subscription without <see cref="TenantId"/> only receives events when this flag is true; it then receives
    /// events of every tenant as well as tenant-less (global) events. Defaults to false (fail-closed).
    /// </summary>
    public bool IsClusterWide { get; init; }

    public bool IsActive { get; init; } = true;

    /// <summary>Delivery attempts per event. Values are clamped to 1..<see cref="MaxAllowedAttempts"/> (SR-M-13).</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>Upper bound for <see cref="MaxAttempts"/>.</summary>
    public const int MaxAllowedAttempts = 20;

    /// <summary>The effective, bounded number of delivery attempts.</summary>
    public int EffectiveMaxAttempts => Math.Clamp(MaxAttempts, 1, MaxAllowedAttempts);

    public bool MatchesEvent(string eventType, string? eventTenantId)
    {
        if (!IsActive) return false;

        // Tenant isolation (fail-closed, SR-M-12): a tenant-scoped subscription only receives events of that tenant;
        // a subscription without a tenant must be explicitly marked cluster-wide to receive anything.
        if (!string.IsNullOrWhiteSpace(TenantId))
        {
            if (!string.Equals(TenantId, eventTenantId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        else if (!IsClusterWide)
        {
            return false;
        }

        // Empty filter or "*" matches all events
        if (EventFilters == null || EventFilters.Count == 0 || EventFilters.Contains("*"))
        {
            return true;
        }

        foreach (var filter in EventFilters)
        {
            if (string.Equals(filter, eventType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Prefix matching, e.g. "governance.*"
            if (filter.EndsWith(".*", StringComparison.Ordinal))
            {
                var prefix = filter[..^2];
                if (eventType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
