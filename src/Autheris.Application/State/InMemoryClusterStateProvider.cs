namespace Autheris.Application.State;

using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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

    public ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_store.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > DateTimeOffset.UtcNow)
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

        var expiresAt = (ttl <= TimeSpan.Zero || ttl == TimeSpan.MaxValue) ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.Add(ttl);
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

        long result = 0;
        var expiresAt = (ttl <= TimeSpan.Zero || ttl == TimeSpan.MaxValue) ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.Add(ttl);
        _store.AddOrUpdate(
            key,
            _ =>
            {
                result = delta;
                return new Entry(delta.ToString(System.Globalization.CultureInfo.InvariantCulture), expiresAt);
            },
            (_, existing) =>
            {
                var live = existing.ExpiresAt > DateTimeOffset.UtcNow;
                var current = live ? long.Parse(existing.Serialized, System.Globalization.CultureInfo.InvariantCulture) : 0;
                result = current + delta;
                return new Entry(
                    result.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    live ? existing.ExpiresAt : expiresAt);
            });

        return ValueTask.FromResult<long?>(result);
    }

    private readonly object _budgetLock = new();

    public ValueTask<(BudgetConsumeOutcome Outcome, long ConsumedAfter)> TryConsumeBudgetAsync(
        string key, long cost, long limit, TimeSpan ttl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_budgetLock)
        {
            var now = DateTimeOffset.UtcNow;
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
                    await handler(payload).ConfigureAwait(false);
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
