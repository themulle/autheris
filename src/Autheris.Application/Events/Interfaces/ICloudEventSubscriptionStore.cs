namespace Autheris.Application.Events.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// F-EVT-01: Multi-tenant subscription store for outbound CloudEvent webhooks.
/// </summary>
public interface ICloudEventSubscriptionStore
{
    /// <summary>Default per-tenant subscription quota used by <see cref="RegisterSubscriptionAsync(CloudEventWebhookSubscription, CancellationToken)"/>.</summary>
    const int DefaultMaxSubscriptionsPerTenant = 100;

    /// <summary>
    /// Stores a new subscription. Throws <see cref="System.InvalidOperationException"/> when the tenant already holds
    /// <paramref name="maxSubscriptionsPerTenant"/> subscriptions, or when the id is already taken (never overwrites a row).
    /// </summary>
    ValueTask RegisterSubscriptionAsync(CloudEventWebhookSubscription subscription, int maxSubscriptionsPerTenant, CancellationToken ct = default);

    ValueTask RegisterSubscriptionAsync(CloudEventWebhookSubscription subscription, CancellationToken ct = default) =>
        RegisterSubscriptionAsync(subscription, DefaultMaxSubscriptionsPerTenant, ct);
    ValueTask<bool> RemoveSubscriptionAsync(string tenantId, string subscriptionId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<CloudEventWebhookSubscription>> GetSubscriptionsAsync(string tenantId, string tableName, CdcOperation operation, CancellationToken ct = default);
    ValueTask<IReadOnlyList<CloudEventWebhookSubscription>> GetSubscriptionsAsync(string tenantId, TableIdentifier table, CdcOperation operation, CancellationToken ct = default);
    /// <summary>Resolves one subscription of exactly this tenant (used at delivery time to read URL, secret and headers); <c>null</c> when unknown.</summary>
    ValueTask<CloudEventWebhookSubscription?> GetSubscriptionAsync(string tenantId, string subscriptionId, CancellationToken ct = default);

    ValueTask<IReadOnlyList<CloudEventWebhookSubscription>> ListSubscriptionsAsync(string tenantId, CancellationToken ct = default);
}
