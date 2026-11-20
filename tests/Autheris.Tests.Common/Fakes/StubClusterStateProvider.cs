using Autheris.Application.State;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// Configurable <see cref="IDistributedClusterStateProvider"/> backed by <see cref="InMemoryClusterStateProvider"/>
/// with optional per-method overrides for lease, lock, increment, and budget simulation.
/// </summary>
public sealed class StubClusterStateProvider(TimeProvider? timeProvider = null) : IDistributedClusterStateProvider
{
    private readonly InMemoryClusterStateProvider _inner = new(timeProvider);

    public Func<string, TimeSpan, CancellationToken, ValueTask<IDistributedLease?>>? OnTryAcquireLease { get; init; }
    public Func<string, TimeSpan, CancellationToken, ValueTask<IAsyncDisposable?>>? OnTryAcquireLock { get; init; }
    public Func<string, long, TimeSpan, CancellationToken, ValueTask<long?>>? OnIncrement { get; init; }
    public Func<string, long, long, TimeSpan, CancellationToken, ValueTask<(BudgetConsumeOutcome Outcome, long ConsumedAfter)>>? OnTryConsumeBudget { get; init; }

    public ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default)
        => _inner.GetAsync<T>(key, ct);

    public ValueTask SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default)
        => _inner.SetAsync(key, value, ttl, ct);

    public ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default)
        => _inner.RemoveAsync(key, ct);

    public ValueTask<long?> IncrementAsync(string key, long delta, TimeSpan ttl, CancellationToken ct = default)
        => OnIncrement is not null ? OnIncrement(key, delta, ttl, ct) : _inner.IncrementAsync(key, delta, ttl, ct);

    public ValueTask<(BudgetConsumeOutcome Outcome, long ConsumedAfter)> TryConsumeBudgetAsync(
        string key, long cost, long limit, TimeSpan ttl, CancellationToken ct = default)
        => OnTryConsumeBudget is not null
            ? OnTryConsumeBudget(key, cost, limit, ttl, ct)
            : _inner.TryConsumeBudgetAsync(key, cost, limit, ttl, ct);

    public ValueTask PublishEventAsync<T>(string channel, T payload, CancellationToken ct = default)
        => _inner.PublishEventAsync(channel, payload, ct);

    public IAsyncDisposable SubscribeAsync<T>(string channel, Func<T, ValueTask> handler, CancellationToken ct = default)
        => _inner.SubscribeAsync(channel, handler, ct);

    public ValueTask<IAsyncDisposable?> TryAcquireLockAsync(string resourceKey, TimeSpan expiry, CancellationToken ct = default)
        => OnTryAcquireLock is not null
            ? OnTryAcquireLock(resourceKey, expiry, ct)
            : _inner.TryAcquireLockAsync(resourceKey, expiry, ct);

    public ValueTask<IDistributedLease?> TryAcquireLeaseAsync(string resourceKey, TimeSpan ttl, CancellationToken ct = default)
        => OnTryAcquireLease is not null
            ? OnTryAcquireLease(resourceKey, ttl, ct)
            : _inner.TryAcquireLeaseAsync(resourceKey, ttl, ct);

    public ValueTask<bool?> TryAcquireSlotAsync(string setKey, string member, int limit, TimeSpan ttl, CancellationToken ct = default)
        => _inner.TryAcquireSlotAsync(setKey, member, limit, ttl, ct);

    public ValueTask<bool> RenewSlotAsync(string setKey, string member, TimeSpan ttl, CancellationToken ct = default)
        => _inner.RenewSlotAsync(setKey, member, ttl, ct);

    public ValueTask ReleaseSlotAsync(string setKey, string member, CancellationToken ct = default)
        => _inner.ReleaseSlotAsync(setKey, member, ct);
}
