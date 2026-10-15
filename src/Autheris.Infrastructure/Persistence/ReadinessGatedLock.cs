using Autheris.Infrastructure.Persistence.Migrations;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// A mutual-exclusion gate that refuses to be entered while its owning governance store is not ready (WP-ARC-B-12).
/// Only the migration runner and initialization code use <see cref="WaitUnguardedAsync"/>.
/// </summary>
internal sealed class ReadinessGatedLock(Func<bool> isReady, string store) : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>Throws <see cref="GovernanceStoreNotReadyException"/> while the store is not ready, otherwise waits for the gate.</summary>
    public Task WaitAsync(CancellationToken ct)
    {
        if (!isReady())
        {
            throw new GovernanceStoreNotReadyException(store);
        }

        return _semaphore.WaitAsync(ct);
    }

    /// <summary>Waits for the gate without the readiness check (migration runner and initialization only).</summary>
    public Task WaitUnguardedAsync(CancellationToken ct) => _semaphore.WaitAsync(ct);

    public void Release() => _semaphore.Release();

    public void Dispose() => _semaphore.Dispose();
}
