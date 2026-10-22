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
/// Contract: <see cref="GetAsync{T}"/> returns <c>default</c> only when the key is absent. Infrastructure failures of
/// <c>GetAsync</c>, <c>SetAsync</c>, <c>RemoveAsync</c>, <c>PublishEventAsync</c> and <c>TryAcquireLockAsync</c> throw
/// <see cref="ClusterStateUnavailableException"/>; a <c>null</c> lock lease means "busy" only. Counters and budgets keep
/// their explicit unavailable signals (<c>null</c> / <see cref="BudgetConsumeOutcome.StoreUnavailable"/>).
/// </summary>
public interface IDistributedClusterStateProvider
{
    ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default);

    ValueTask SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default);

    ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Atomically adds <paramref name="delta"/> to a cluster-wide counter and returns the new value, or null when the
    /// shared store is unreachable (<c>null</c> means unavailable, never "zero"; callers must fall back to local accounting or fail closed). The TTL is set when the counter is created.
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
    /// Tries to acquire a distributed lock for atomicity across nodes. Returns an IAsyncDisposable lease, or null only if the lock is busy.
    /// </summary>
    ValueTask<IAsyncDisposable?> TryAcquireLockAsync(string resourceKey, TimeSpan expiry, CancellationToken ct = default);

    /// <summary>
    /// Tries to acquire a renewed, fenced lease for <paramref name="resourceKey"/>. The lease is extended in the background
    /// every <c>ttl / 3</c> until disposed; <see cref="IDistributedLease.LeaseLost"/> fires when ownership can no longer be
    /// guaranteed. Returns <c>null</c> only when the lease is held by someone else; an unreachable store throws
    /// <see cref="ClusterStateUnavailableException"/>.
    /// </summary>
    ValueTask<IDistributedLease?> TryAcquireLeaseAsync(string resourceKey, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>
    /// GQL-10: Atomically attempts to acquire an expiring slot member in setKey if count of active (unexpired) slots &lt; limit.
    /// Returns true if acquired, false if at limit, or null if store is unreachable.
    /// </summary>
    ValueTask<bool?> TryAcquireSlotAsync(string setKey, string member, int limit, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>
    /// GQL-10: Renews the slot member expiry by ttl. Returns true if renewed, false if not found / expired.
    /// </summary>
    ValueTask<bool> RenewSlotAsync(string setKey, string member, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>
    /// GQL-10: Releases the slot member from setKey.
    /// </summary>
    ValueTask ReleaseSlotAsync(string setKey, string member, CancellationToken ct = default);
}
