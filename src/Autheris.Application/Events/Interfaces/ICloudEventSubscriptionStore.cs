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
    ValueTask RegisterSubscriptionAsync(CloudEventWebhookSubscription subscription, CancellationToken ct = default);
    ValueTask<bool> RemoveSubscriptionAsync(string tenantId, string subscriptionId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<CloudEventWebhookSubscription>> GetSubscriptionsAsync(string tenantId, string tableName, CdcOperation operation, CancellationToken ct = default);
    ValueTask<IReadOnlyList<CloudEventWebhookSubscription>> GetSubscriptionsAsync(string tenantId, TableIdentifier table, CdcOperation operation, CancellationToken ct = default);
    /// <summary>Resolves one subscription of exactly this tenant (used at delivery time to read URL, secret and headers); <c>null</c> when unknown.</summary>
    ValueTask<CloudEventWebhookSubscription?> GetSubscriptionAsync(string tenantId, string subscriptionId, CancellationToken ct = default);

    ValueTask<IReadOnlyList<CloudEventWebhookSubscription>> ListSubscriptionsAsync(string tenantId, CancellationToken ct = default);
}
