namespace Autheris.Application.State;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// K-K14: In-Memory reference implementation of IDistributedClusterStateProvider.
/// Used for single-node deployments, dev environments, and integration tests.
/// </summary>
public class InMemoryClusterStateProvider : IDistributedClusterStateProvider
{
    private sealed record Entry(string Serialized, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _store = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentBag<Delegate>> _subscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly object _budgetLock = new();
    private readonly object _leaseLock = new();
    private readonly Dictionary<string, LeaseEntry> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _fences = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public InMemoryClusterStateProvider(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    private sealed record LeaseEntry(Guid Owner, long Token, DateTimeOffset ExpiresAt);

    public ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_store.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > _time.GetUtcNow())
            {
                var val = JsonSerializer.Deserialize<T>(entry.Serialized);
                return ValueTask.FromResult(val);
            }

            _store.TryRemove(key, out _);
        }

        return ValueTask.FromResult<T?>(default);
    }

    public ValueTask SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        var expiresAt = (ttl <= TimeSpan.Zero || ttl == TimeSpan.MaxValue) ? DateTimeOffset.MaxValue : _time.GetUtcNow().Add(ttl);
        var serialized = JsonSerializer.Serialize(value);
        _store[key] = new Entry(serialized, expiresAt);

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return ValueTask.FromResult(_store.TryRemove(key, out _));
    }

    public ValueTask<long?> IncrementAsync(string key, long delta, TimeSpan ttl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // All numeric operations share _budgetLock so a concurrent increment and budget consume cannot lose updates.
        lock (_budgetLock)
        {
            var now = _time.GetUtcNow();
            var newExpiry = (ttl <= TimeSpan.Zero || ttl == TimeSpan.MaxValue) ? DateTimeOffset.MaxValue : now.Add(ttl);
            long result;
            DateTimeOffset expiresAt;
            if (_store.TryGetValue(key, out var existing) &&
                existing.ExpiresAt > now &&
                long.TryParse(existing.Serialized, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var current))
            {
                result = current + delta;
                expiresAt = existing.ExpiresAt;
            }
            else
            {
                result = delta;
                expiresAt = newExpiry;
            }

            _store[key] = new Entry(result.ToString(System.Globalization.CultureInfo.InvariantCulture), expiresAt);
            return ValueTask.FromResult<long?>(result);
        }
    }

    public ValueTask<(BudgetConsumeOutcome Outcome, long ConsumedAfter)> TryConsumeBudgetAsync(
        string key, long cost, long limit, TimeSpan ttl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_budgetLock)
        {
            var now = _time.GetUtcNow();
            long current = 0;
            DateTimeOffset expiresAt = (ttl <= TimeSpan.Zero || ttl == TimeSpan.MaxValue) ? DateTimeOffset.MaxValue : now.Add(ttl);

            if (_store.TryGetValue(key, out var existing))
            {
                if (existing.ExpiresAt > now)
                {
                    if (long.TryParse(existing.Serialized, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    {
                        current = parsed;
                    }
                    expiresAt = existing.ExpiresAt;
                }
            }

            if (current + cost <= limit)
            {
                var newTotal = current + cost;
                _store[key] = new Entry(newTotal.ToString(System.Globalization.CultureInfo.InvariantCulture), expiresAt);
                return ValueTask.FromResult((BudgetConsumeOutcome.Consumed, newTotal));
            }
            else
            {
                return ValueTask.FromResult((BudgetConsumeOutcome.Exhausted, current));
            }
        }
    }

    public async ValueTask PublishEventAsync<T>(string channel, T payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);

        if (_subscriptions.TryGetValue(channel, out var handlers))
        {
            foreach (var del in handlers)
            {
                if (del is Func<T, ValueTask> handler)
                {
                    try
                    {
                        await handler(payload).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // A failing subscriber must not prevent delivery to the other subscribers or fail the publisher.
                    }
                }
            }
        }
    }

    public IAsyncDisposable SubscribeAsync<T>(string channel, Func<T, ValueTask> handler, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(handler);

        var bag = _subscriptions.GetOrAdd(channel, _ => new ConcurrentBag<Delegate>());
        bag.Add(handler);

        return new SubscriptionLease(() =>
        {
            // Simple lease
            return ValueTask.CompletedTask;
        });
    }

    public async ValueTask<IAsyncDisposable?> TryAcquireLockAsync(string resourceKey, TimeSpan expiry, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);

        var sem = _locks.GetOrAdd(resourceKey, _ => new SemaphoreSlim(1, 1));
        var entered = await sem.WaitAsync(0, ct).ConfigureAwait(false);
        if (!entered)
        {
            return null;
        }

        return new LockLease(sem);
    }

    public ValueTask<IDistributedLease?> TryAcquireLeaseAsync(string resourceKey, TimeSpan ttl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);

        var owner = Guid.NewGuid();
        long token;
        lock (_leaseLock)
        {
            var now = _time.GetUtcNow();
            if (_leases.TryGetValue(resourceKey, out var held) && held.ExpiresAt > now)
            {
                return ValueTask.FromResult<IDistributedLease?>(null);
            }

            // Seeded from the wall clock so tokens stay above those of a previous process generation (durable stores remember them).
            token = _fences.TryGetValue(resourceKey, out var last) ? last + 1 : _time.GetUtcNow().ToUnixTimeMilliseconds() * 1000;
            _fences[resourceKey] = token;
            _leases[resourceKey] = new LeaseEntry(owner, token, now.Add(ttl));
        }

        return ValueTask.FromResult<IDistributedLease?>(new InMemoryLease(this, resourceKey, owner, token, ttl, _time));
    }

    private bool ExtendLease(string resourceKey, Guid owner, TimeSpan ttl)
    {
        lock (_leaseLock)
        {
            var now = _time.GetUtcNow();
            if (_leases.TryGetValue(resourceKey, out var held) && held.Owner == owner && held.ExpiresAt > now)
            {
                _leases[resourceKey] = held with { ExpiresAt = now.Add(ttl) };
                return true;
            }

            return false;
        }
    }

    private void ReleaseLease(string resourceKey, Guid owner)
    {
        lock (_leaseLock)
        {
            if (_leases.TryGetValue(resourceKey, out var held) && held.Owner == owner)
            {
                _leases.Remove(resourceKey);
            }
        }
    }

    private sealed class InMemoryLease : DistributedLeaseBase
    {
        private readonly InMemoryClusterStateProvider _owner;
        private readonly Guid _ownerId;
        private readonly TimeSpan _ttl;

        public InMemoryLease(InMemoryClusterStateProvider owner, string resourceKey, Guid ownerId, long token, TimeSpan ttl, TimeProvider time)
            : base(time, ttl, resourceKey, token, NullLogger.Instance)
        {
            _owner = owner;
            _ownerId = ownerId;
            _ttl = ttl;
            StartHeartbeat();
        }

        protected override ValueTask<bool> TryExtendAsync(CancellationToken ct) =>
            ValueTask.FromResult(_owner.ExtendLease(ResourceKey, _ownerId, _ttl));

        protected override ValueTask ReleaseAsync()
        {
            _owner.ReleaseLease(ResourceKey, _ownerId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SubscriptionLease : IAsyncDisposable
    {
        private readonly Func<ValueTask> _dispose;
        public SubscriptionLease(Func<ValueTask> dispose) => _dispose = dispose;
        public ValueTask DisposeAsync() => _dispose();
    }

    private sealed class LockLease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private int _disposed;

        public LockLease(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _semaphore.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
