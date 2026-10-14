namespace Autheris.Domain.Options;

using System;
using System.Collections.Generic;

public sealed class WebhooksOptions
{
    public bool Enabled { get; init; } = true;
    public int DefaultTimeoutSeconds { get; init; } = 5;
    public List<WebhookSubscription> Subscriptions { get; init; } = [];
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

    /// <summary>Delivery attempts per event. Values are clamped to 1..<see cref="MaxAllowedRetries"/> (SR-M-13).</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Upper bound for <see cref="MaxRetries"/>.</summary>
    public const int MaxAllowedRetries = 10;

    /// <summary>The effective, bounded number of delivery attempts.</summary>
    public int EffectiveMaxRetries => Math.Clamp(MaxRetries, 1, MaxAllowedRetries);

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
