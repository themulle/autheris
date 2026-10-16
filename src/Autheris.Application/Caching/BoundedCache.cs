namespace Autheris.Application.Caching;

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using BitFaster.Caching.Lru;

/// <summary>
/// WP-PF-06: size-bounded cache for per-user and per-table memo structures (BitFaster <c>ConcurrentLru</c>: lock-free reads,
/// O(1) eviction). Eviction only ever produces a miss, so callers must treat a miss as "recompute from the authoritative source".
/// An optional absolute TTL uses the injected <see cref="TimeProvider"/> so expiry is deterministic in tests.
/// <see cref="GetOrAddAsync"/> is single-flight per key: concurrent misses share one factory call; a failed factory is not cached.
/// </summary>
/// <typeparam name="TKey">Key type.</typeparam>
/// <typeparam name="TValue">Value type.</typeparam>
public sealed class BoundedCache<TKey, TValue>
    where TKey : notnull
{
    private sealed record Entry(TValue Value, long CreatedTimestamp);

    private readonly ConcurrentLru<TKey, Entry> _lru;
    private readonly ConcurrentDictionary<TKey, Lazy<Task<TValue>>> _inFlight = new();
    private readonly TimeSpan? _ttl;
    private readonly TimeProvider _time;

    /// <param name="capacity">Maximum number of entries (at least 3).</param>
    /// <param name="ttl">Absolute lifetime of an entry; <c>null</c> for none.</param>
    /// <param name="time">Clock for the TTL (default: system).</param>
    /// <param name="comparer">Key comparer (default: the key type's default).</param>
    public BoundedCache(int capacity, TimeSpan? ttl = null, TimeProvider? time = null, System.Collections.Generic.IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 3);
        if (ttl is { } t)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(t, TimeSpan.Zero);
        }

        _ttl = ttl;
        _time = time ?? TimeProvider.System;
        _lru = new ConcurrentLru<TKey, Entry>(1, capacity, comparer ?? System.Collections.Generic.EqualityComparer<TKey>.Default);
    }

    /// <summary>Current number of entries (never above the capacity, apart from transient in-flight inserts).</summary>
    public int Count => _lru.Count;

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
        value = default;
        return _lru.TryRemove(key);
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

    public async Task<TValue> GetOrAddAsync(TKey key, Func<TKey, Task<TValue>> factory)
    {
        if (TryGet(key, out var existing))
        {
            return existing;
        }

        var lazy = _inFlight.GetOrAdd(key, k => new Lazy<Task<TValue>>(() => RunAsync(k, factory)));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(new System.Collections.Generic.KeyValuePair<TKey, Lazy<Task<TValue>>>(key, lazy));
        }
    }

    private async Task<TValue> RunAsync(TKey key, Func<TKey, Task<TValue>> factory)
    {
        var value = await factory(key).ConfigureAwait(false);
        Set(key, value);
        return value;
    }
}
