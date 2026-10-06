namespace Autheris.Application.State;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// K-K14: Pluggable Distributed Cluster State Provider for Multi-Node Deployments.
/// Provides distributed KV storage, pub/sub messaging for instant cache invalidation,
/// and distributed resource locks for cluster-wide consistency.
/// </summary>
public interface IDistributedClusterStateProvider
{
    ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default);

    ValueTask SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default);

    ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Atomically adds <paramref name="delta"/> to a cluster-wide counter and returns the new value, or null when the
    /// shared store is unreachable (callers must then fall back to local accounting). The TTL is set when the counter is created.
    /// </summary>
    ValueTask<long?> IncrementAsync(string key, long delta, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>
    /// Publishes a broadcast event across all cluster nodes (e.g. token revocation, HitL step-up approval).
    /// </summary>
    ValueTask PublishEventAsync<T>(string channel, T payload, CancellationToken ct = default);

    /// <summary>
    /// Subscribes to cluster broadcast events on the specified channel.
    /// </summary>
    IAsyncDisposable SubscribeAsync<T>(string channel, Func<T, ValueTask> handler, CancellationToken ct = default);

    /// <summary>
    /// Tries to acquire a distributed lock for atomicity across nodes. Returns an IAsyncDisposable lease or null if busy.
    /// </summary>
    ValueTask<IAsyncDisposable?> TryAcquireLockAsync(string resourceKey, TimeSpan expiry, CancellationToken ct = default);
}
