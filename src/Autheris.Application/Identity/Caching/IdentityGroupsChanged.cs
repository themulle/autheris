namespace Autheris.Application.Identity.Caching;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Invalidation hint for the group resolution cache. Carries no group names or SIDs. A message for a whole realm, with more than
/// <see cref="IdentityGroupsChangedChannel.MaxUserKeys"/> user keys or after a group delete uses the <see cref="InvalidateAll"/> variant.
/// </summary>
public sealed record IdentityGroupsChanged(string RealmId, string[] UserKeys, bool InvalidateAll);

public static class IdentityGroupsChangedChannel
{
    public const string Name = "autheris:identity:groups-changed";
    public const int MaxUserKeys = 256;
    public const int MaxKeyLength = 256;
}

/// <summary>
/// Local invalidation also goes out on <see cref="IEventBus"/> (best effort, no delivery guarantee). The cache TTL is the correctness
/// backstop; the event only makes other nodes drop an entry earlier. A publish failure is logged and never fails the caller.
/// </summary>
public sealed class BroadcastingRoleResolverCache(RoleResolverCache local, IEventBus? eventBus = null, ILogger<BroadcastingRoleResolverCache>? logger = null) : IRoleResolverCache
{
    public ValueTask<RoleResolution?> GetAsync(string realmId, string userKey, CancellationToken ct = default) => local.GetAsync(realmId, userKey, ct);

    public ValueTask SetAsync(string realmId, string userKey, RoleResolution resolution, CancellationToken ct = default) => local.SetAsync(realmId, userKey, resolution, ct);

    public void Invalidate(string realmId, string userKey)
    {
        local.Invalidate(realmId, userKey);
        var tooLong = realmId.Length > IdentityGroupsChangedChannel.MaxKeyLength || userKey.Length > IdentityGroupsChangedChannel.MaxKeyLength;
        Publish(tooLong
            ? new IdentityGroupsChanged(string.Empty, [], true)
            : new IdentityGroupsChanged(realmId, [userKey], false));
    }

    public void InvalidateAll()
    {
        local.InvalidateAll();
        Publish(new IdentityGroupsChanged(string.Empty, [], true));
    }

    private void Publish(IdentityGroupsChanged message)
    {
        if (eventBus is null)
        {
            return;
        }

        _ = PublishAsync(message);
    }

    private async Task PublishAsync(IdentityGroupsChanged message)
    {
        try
        {
            await eventBus!.PublishAsync(IdentityGroupsChangedChannel.Name, message).ConfigureAwait(false);
        }
        catch (Exception)
        {
            IdentityGroupMetrics.RecordPublishFailed();
            logger?.LogWarning("Publishing the identity group invalidation failed; the cache entry expires with its TTL.");
        }
    }
}

/// <summary>Applies <see cref="IdentityGroupsChanged"/> to the node-local cache. Anything unexpected invalidates everything (fail closed).</summary>
public sealed class IdentityGroupsChangedSubscriber(RoleResolverCache local, IEventBus eventBus, ILogger<IdentityGroupsChangedSubscriber>? logger = null) : IHostedService, IDisposable
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Hooks first: a failing Subscribe must not leave a node that never learns about a restored connection.
        eventBus.ConnectionRestored += local.InvalidateAll;
        if (eventBus is IEventBusDeliveryFailures failures)
        {
            failures.DeliveryFailed += OnDeliveryFailed;
        }

        try
        {
            _subscription ??= eventBus.Subscribe<IdentityGroupsChanged>(IdentityGroupsChangedChannel.Name, Handle);
        }
        catch (Exception ex)
        {
            // Without a subscription the node relies on the TTL; whatever it holds now is dropped.
            local.InvalidateAll();
            logger?.LogError(ex, "Subscribing to the identity group invalidation channel failed; cached groups expire with their TTL.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        eventBus.ConnectionRestored -= local.InvalidateAll;
        if (eventBus is IEventBusDeliveryFailures failures)
        {
            failures.DeliveryFailed -= OnDeliveryFailed;
        }

        _subscription?.Dispose();
        _subscription = null;
    }

    private void OnDeliveryFailed(string channel)
    {
        // A message on our channel that could not be delivered may have been an invalidation: drop everything.
        if (string.Equals(channel, IdentityGroupsChangedChannel.Name, StringComparison.Ordinal))
        {
            local.InvalidateAll();
        }
    }

    internal Task Handle(IdentityGroupsChanged? message)
    {
        try
        {
            if (message is null || message.InvalidateAll || string.IsNullOrEmpty(message.RealmId)
                || message.RealmId.Length > IdentityGroupsChangedChannel.MaxKeyLength
                || message.UserKeys is null || message.UserKeys.Length > IdentityGroupsChangedChannel.MaxUserKeys
                || Array.Exists(message.UserKeys, k => string.IsNullOrEmpty(k) || k.Length > IdentityGroupsChangedChannel.MaxKeyLength))
            {
                local.InvalidateAll();
                return Task.CompletedTask;
            }

            foreach (var key in message.UserKeys)
            {
                local.Invalidate(message.RealmId, key);
            }
        }
        catch (Exception)
        {
            local.InvalidateAll();
        }

        return Task.CompletedTask;
    }
}
