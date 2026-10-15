namespace Autheris.Application.Webhooks;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// Resolves target URL, secret and custom headers of a subscription at delivery time (the outbox row stores none of them).
/// CloudEvent subscriptions are looked up scoped to the owning tenant. The CloudEvent store is optional.
/// </summary>
public sealed class WebhookSubscriptionResolver(IOptions<GatewayOptions> options, IServiceProvider services)
{
    /// <summary>Returns <c>null</c> when the subscription no longer exists (or cannot be looked up).</summary>
    public async ValueTask<ResolvedWebhookTarget?> ResolveAsync(WebhookOutboxMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        switch (message.Source)
        {
            case WebhookSubscriptionSource.Configured:
                var configured = options.Value.Webhooks.Subscriptions
                    .FirstOrDefault(s => string.Equals(s.Id, message.SubscriptionId, StringComparison.Ordinal));
                if (configured is null)
                {
                    return null;
                }

                if (!configured.IsActive)
                {
                    return new ResolvedWebhookTarget(configured.TargetUrl, configured.Secret, null, false);
                }

                // The subscription may have been re-bound since the row was queued: retries must never leave the tenant or filter
                // the row was matched under (fail closed, same as a missing subscription).
                var owner = string.IsNullOrWhiteSpace(configured.TenantId) ? null : configured.TenantId;
                return string.Equals(owner, message.OwnerTenantId, StringComparison.OrdinalIgnoreCase)
                       && configured.MatchesEvent(message.EventType, message.EventTenantId)
                    ? new ResolvedWebhookTarget(configured.TargetUrl, configured.Secret, null, true)
                    : null;

            case WebhookSubscriptionSource.CloudEvent:
                var store = services.GetService<ICloudEventSubscriptionStore>();
                if (store is null || string.IsNullOrWhiteSpace(message.OwnerTenantId))
                {
                    return null;
                }

                var subscription = await store.GetSubscriptionAsync(message.OwnerTenantId, message.SubscriptionId, ct).ConfigureAwait(false);
                return subscription is null
                    ? null
                    : new ResolvedWebhookTarget(subscription.TargetUrl, UnprotectSecret(subscription, store), UnprotectHeaders(subscription), subscription.IsEnabled);

            default:
                return null;
        }
    }

    /// <summary>
    /// Stored secrets are KEK-protected (<c>enc:v1:</c>). A protected value that cannot be unprotected throws (never signs with the
    /// ciphertext); the row's lease expires and the poison-attempt budget bounds the retries. A non-empty value without the prefix is
    /// accepted only from the in-memory (test) store; a durable store fails closed.
    /// </summary>
    private string UnprotectSecret(CloudEventWebhookSubscription subscription, ICloudEventSubscriptionStore store)
    {
        var stored = subscription.HmacSecret;
        if (string.IsNullOrEmpty(stored))
        {
            return string.Empty;
        }

        if (!stored.StartsWith("enc:v1:", StringComparison.Ordinal))
        {
            return store is Autheris.Application.Events.Services.InMemoryCloudEventSubscriptionStore
                ? stored
                : throw new WebhookSecretUnavailableException();
        }

        var protector = services.GetService<IWebhookSecretProtector>() ?? throw new WebhookSecretUnavailableException();
        return protector.Unprotect(subscription.TenantId, subscription.Id, stored);
    }

    private System.Collections.Generic.IReadOnlyDictionary<string, string>? UnprotectHeaders(CloudEventWebhookSubscription subscription)
    {
        if (!Autheris.Application.Events.Services.CdcCustomHeaders.IsProtected(subscription.CustomHeaders))
        {
            return subscription.CustomHeaders;
        }

        var protector = services.GetService<IWebhookSecretProtector>() ?? throw new WebhookSecretUnavailableException();
        var json = protector.Unprotect(subscription.TenantId, subscription.Id, subscription.CustomHeaders![Autheris.Application.Events.Services.CdcCustomHeaders.ProtectedKey]);
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new WebhookSecretUnavailableException(ex);
        }
    }
}
