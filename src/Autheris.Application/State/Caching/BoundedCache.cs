namespace Autheris.Application.State.Caching;

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using BitFaster.Caching.Lru;

/// <summary>
/// size-bounded cache for per-user and per-table memo structures (BitFaster<c>ConcurrentLru</c>: lock-free reads,
/// O(1) eviction). Eviction only ever produces a miss, so callers must treat a miss as "recompute from the authoritative source".
/// An optional absolute TTL uses the injected <see cref="TimeProvider"/> so expiry is deterministic in tests.
/// <see cref="GetOrAddAsync"/> is single-flight per key: concurrent misses share one factory call; a failed factory is not cached.
/// </summary>
/// <typeparam name="TKey">Key type.</typeparam>
/// <typeparam name="TValue">Value type.</typeparam>
public sealed class BoundedCache<TKey, TValue>
    where TKey : notnull
{
    private static readonly TimeSpan DefaultFactoryTimeout = TimeSpan.FromSeconds(30);

    private sealed record Entry(TValue Value, long CreatedTimestamp);

    private sealed class InFlightEntry
    {
        public TaskCompletionSource<TValue> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public InFlightEntry()
        {
            _ = Completion.Task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    private readonly ConcurrentLru<TKey, Entry> _lru;
    private readonly ConcurrentDictionary<TKey, InFlightEntry> _inFlight;
    private readonly TimeSpan? _ttl;
    private readonly TimeProvider _time;
    private readonly TimeSpan _factoryTimeout;

    /// <param name="capacity">Maximum number of entries (at least 3).</param>
    /// <param name="ttl">Absolute lifetime of an entry; <c>null</c> for none.</param>
    /// <param name="time">Clock for the TTL (default: system).</param>
    /// <param name="comparer">Key comparer (default: the key type's default).</param>
    /// <param name="factoryTimeout">Timeout applied to a shared <see cref="GetOrAddAsync"/> factory run (default: 30s).</param>
    public BoundedCache(
        int capacity,
        TimeSpan? ttl = null,
        TimeProvider? time = null,
        System.Collections.Generic.IEqualityComparer<TKey>? comparer = null,
        TimeSpan? factoryTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 3);
        if (ttl is { } t)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(t, TimeSpan.Zero);
        }

        if (factoryTimeout is { } ft)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ft, TimeSpan.Zero);
        }

        _ttl = ttl;
        _time = time ?? TimeProvider.System;
        _factoryTimeout = factoryTimeout ?? DefaultFactoryTimeout;
        var keyComparer = comparer ?? System.Collections.Generic.EqualityComparer<TKey>.Default;
        _lru = new ConcurrentLru<TKey, Entry>(1, capacity, keyComparer);
        _inFlight = new ConcurrentDictionary<TKey, InFlightEntry>(keyComparer);
    }

    /// <summary>Current number of entries (never above the capacity, apart from transient in-flight inserts).</summary>
    public int Count => _lru.Count;

    /// <summary>Snapshot of the keys currently held (does not promote entries; expired entries may still be listed until read).</summary>
    public System.Collections.Generic.IEnumerable<TKey> Keys => _lru.Keys;

    public bool TryGet(TKey key, out TValue value)
    {
        if (_lru.TryGet(key, out var entry))
        {
            if (_ttl is not { } ttl || _time.GetElapsedTime(entry.CreatedTimestamp) <= ttl)
            {
                value = entry.Value;
                return true;
            }

            _lru.TryRemove(key);
        }

        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value) => _lru.AddOrUpdate(key, new Entry(value, _time.GetTimestamp()));

    public bool TryRemove(TKey key) => _lru.TryRemove(key);

    // ConcurrentDictionary-shaped members so existing call sites keep their shape when a dictionary is replaced by a bounded cache.
    public bool TryGetValue(TKey key, out TValue value) => TryGet(key, out value);

    public bool TryRemove(TKey key, out TValue? value)
    {
        if (_lru.TryRemove(key, out var entry))
        {
            value = entry.Value;
            return true;
        }

        value = default;
        return false;
    }

    public TValue this[TKey key]
    {
        set => Set(key, value);
    }

    public void Clear()
    {
        _lru.Clear();
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        if (TryGet(key, out var existing))
        {
            return existing;
        }

        var created = factory(key);
        Set(key, created);
        return created;
    }

    public Task<TValue> GetOrAddAsync(TKey key, Func<TKey, Task<TValue>> factory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return GetOrAddAsync(key, (k, _) => factory(k), ct);
    }

    public async Task<TValue> GetOrAddAsync(TKey key, Func<TKey, CancellationToken, Task<TValue>> factory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (TryGet(key, out var existing))
        {
            return existing;
        }

        ct.ThrowIfCancellationRequested();
        var created = new InFlightEntry();
        var flight = _inFlight.GetOrAdd(key, created);
        if (ReferenceEquals(flight, created))
        {
            var cts = new CancellationTokenSource(_factoryTimeout, _time);
            _ = Task.Run(() => RunFlightAsync(key, created, factory, cts), CancellationToken.None);
        }

        return await flight.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task RunFlightAsync(
        TKey key,
        InFlightEntry flight,
        Func<TKey, CancellationToken, Task<TValue>> factory,
        CancellationTokenSource cts)
    {
        try
        {
            using (cts)
            {
                if (TryGet(key, out var raced))
                {
                    _inFlight.TryRemove(System.Collections.Generic.KeyValuePair.Create(key, flight));
                    flight.Completion.TrySetResult(raced);
                    return;
                }

                TValue value;
                try
                {
                    var factoryTask = factory(key, cts.Token);
                    _ = factoryTask.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    value = await factoryTask.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    throw new TimeoutException($"BoundedCache factory exceeded its timeout ({_factoryTimeout}).");
                }

                Set(key, value);
                _inFlight.TryRemove(System.Collections.Generic.KeyValuePair.Create(key, flight));
                flight.Completion.TrySetResult(value);
            }
        }
        catch (Exception ex)
        {
            _inFlight.TryRemove(System.Collections.Generic.KeyValuePair.Create(key, flight));
            flight.Completion.TrySetException(ex);
        }
    }
}
