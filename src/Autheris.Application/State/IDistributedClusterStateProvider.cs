namespace Autheris.Application.State;

using System;
using System.Threading;
using System.Threading.Tasks;

public enum BudgetConsumeOutcome
{
    Consumed,
    Exhausted,
    StoreUnavailable
}

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
    /// Atomically checks whether (counter + cost &lt;= limit). If so, increments key by cost, applies TTL,
    /// and returns (Consumed, ConsumedAfter). If limit would be exceeded, returns (Exhausted, currentCounter).
    /// If shared store is unreachable, returns (StoreUnavailable, 0).
    /// </summary>
    ValueTask<(BudgetConsumeOutcome Outcome, long ConsumedAfter)> TryConsumeBudgetAsync(
        string key, long cost, long limit, TimeSpan ttl, CancellationToken ct = default);

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
