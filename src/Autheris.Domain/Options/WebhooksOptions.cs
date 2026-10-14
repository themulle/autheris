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
    public bool IsActive { get; init; } = true;
    public int MaxRetries { get; init; } = 3;

    public bool MatchesEvent(string eventType, string? eventTenantId)
    {
        if (!IsActive) return false;

        // Tenant isolation: If subscription specifies a tenant, event must match
        if (!string.IsNullOrWhiteSpace(TenantId) &&
            !string.Equals(TenantId, eventTenantId, StringComparison.OrdinalIgnoreCase))
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
