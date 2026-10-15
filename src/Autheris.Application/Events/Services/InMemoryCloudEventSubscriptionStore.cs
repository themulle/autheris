namespace Autheris.Application.Events.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// F-EVT-01: In-memory thread-safe store for tenant-scoped outbound webhook subscriptions.
/// </summary>
public sealed class InMemoryCloudEventSubscriptionStore : ICloudEventSubscriptionStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, CloudEventWebhookSubscription>> _subscriptions =
        new(StringComparer.Ordinal);

    public ValueTask RegisterSubscriptionAsync(CloudEventWebhookSubscription subscription, CancellationToken ct = default) =>
        RegisterSubscriptionAsync(subscription, ICloudEventSubscriptionStore.DefaultMaxSubscriptionsPerTenant, ct);

    public ValueTask RegisterSubscriptionAsync(CloudEventWebhookSubscription subscription, int maxSubscriptionsPerTenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscription.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscription.Id);

        var tenantStore = _subscriptions.GetOrAdd(subscription.TenantId, _ => new ConcurrentDictionary<string, CloudEventWebhookSubscription>(StringComparer.Ordinal));

        lock (tenantStore)
        {
            // SEC M-5: Unbounded in-memory subscription store DoS defense
            if (tenantStore.Count >= maxSubscriptionsPerTenant && !tenantStore.ContainsKey(subscription.Id))
            {
                throw new InvalidOperationException($"Maximum webhook subscriptions ({maxSubscriptionsPerTenant}) reached for tenant '{subscription.TenantId}'.");
            }

            tenantStore[subscription.Id] = subscription;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> RemoveSubscriptionAsync(string tenantId, string subscriptionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        if (_subscriptions.TryGetValue(tenantId, out var tenantStore))
        {
            var removed = tenantStore.TryRemove(subscriptionId, out _);
            return ValueTask.FromResult(removed);
        }

        return ValueTask.FromResult(false);
    }

    public ValueTask<IReadOnlyList<CloudEventWebhookSubscription>> GetSubscriptionsAsync(
        string tenantId,
        string tableName,
        CdcOperation operation,
        CancellationToken ct = default)
    {
        if (TableIdentifier.TryParse(tableName, out var tid))
        {
            return GetSubscriptionsAsync(tenantId, tid, operation, ct);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (!_subscriptions.TryGetValue(tenantId, out var tenantStore))
        {
            return ValueTask.FromResult<IReadOnlyList<CloudEventWebhookSubscription>>(Array.Empty<CloudEventWebhookSubscription>());
        }

        var matched = tenantStore.Values.Where(s => CloudEventSubscriptionMatcher.Matches(s, tableName, operation)).ToList();

        return ValueTask.FromResult<IReadOnlyList<CloudEventWebhookSubscription>>(matched);
    }

    public ValueTask<IReadOnlyList<CloudEventWebhookSubscription>> GetSubscriptionsAsync(
        string tenantId,
        TableIdentifier table,
        CdcOperation operation,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (!_subscriptions.TryGetValue(tenantId, out var tenantStore))
        {
            return ValueTask.FromResult<IReadOnlyList<CloudEventWebhookSubscription>>(Array.Empty<CloudEventWebhookSubscription>());
        }

        var matched = tenantStore.Values.Where(s => CloudEventSubscriptionMatcher.Matches(s, table, operation)).ToList();

        return ValueTask.FromResult<IReadOnlyList<CloudEventWebhookSubscription>>(matched);
    }

    public ValueTask<CloudEventWebhookSubscription?> GetSubscriptionAsync(string tenantId, string subscriptionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        // Tenant isolation: the lookup is always scoped to the owning tenant's dictionary.
        return _subscriptions.TryGetValue(tenantId, out var tenantStore) && tenantStore.TryGetValue(subscriptionId, out var subscription)
            ? ValueTask.FromResult<CloudEventWebhookSubscription?>(subscription)
            : ValueTask.FromResult<CloudEventWebhookSubscription?>(null);
    }

    public ValueTask<IReadOnlyList<CloudEventWebhookSubscription>> ListSubscriptionsAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (!_subscriptions.TryGetValue(tenantId, out var tenantStore))
        {
            return ValueTask.FromResult<IReadOnlyList<CloudEventWebhookSubscription>>(Array.Empty<CloudEventWebhookSubscription>());
        }

        var list = tenantStore.Values.ToList();
        return ValueTask.FromResult<IReadOnlyList<CloudEventWebhookSubscription>>(list);
    }
}
