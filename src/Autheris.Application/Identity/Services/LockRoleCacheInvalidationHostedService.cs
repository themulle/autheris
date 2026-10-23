namespace Autheris.Application.Identity.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Caching;
using Microsoft.Extensions.Hosting;

/// <summary>
/// GOV-14: subscribes to the deprovisioning lock channel so that every replica (not only the one that handled the SCIM call) drops the
/// cached role resolution of a locked subject. A failed invalidation falls back to dropping the whole cache (fail closed).
/// </summary>
public sealed class LockRoleCacheInvalidationHostedService(IRedisLockPubSubChannel channel, IRoleResolverCache cache) : IHostedService, IDisposable
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription ??= channel.Subscribe(OnLock);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private void OnLock(LockPubSubMessage message)
    {
        try
        {
            cache.Invalidate(message.RealmId, message.UserKey);
        }
        catch (Exception)
        {
            cache.InvalidateAll();
        }
    }
}
