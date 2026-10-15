namespace Autheris.Application.Governance.Caching;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Autheris.Domain.Interfaces;

/// <summary>
/// Bounded, sharded LRU cache of policy decisions (WP-PF-09). The key space is split over independent shards, each an exact
/// O(1) LRU under its own lock, so hits and misses of different users no longer serialise on one global lock. A small cache
/// (fewer than 512 entries) uses a single shard, which keeps strict LRU order. At capacity only the least recently used entry of
/// a shard is evicted (never a global flush); entries older than the TTL are dropped on access. Time comes from the injected
/// <see cref="TimeProvider"/> so expiry is deterministic in tests. <see cref="GetOrCompute"/> is single-flight per key.
/// </summary>
internal sealed class CasbinDecisionCache
{
    private const int MinEntriesPerShard = 256;
    private const int MaxShards = 32;

    private sealed class Entry(DecisionKey key, TableAccessDecision decision, long createdTimestamp)
    {
        public DecisionKey Key { get; } = key;

        public TableAccessDecision Decision { get; } = decision;

        public long CreatedTimestamp { get; } = createdTimestamp;
    }

    private sealed class Shard(int capacity)
    {
        public readonly object Gate = new();
        public readonly Dictionary<DecisionKey, LinkedListNode<Entry>> Map = new(Math.Min(capacity, 1024));
        public readonly LinkedList<Entry> Lru = new();
        public readonly int Capacity = capacity;
    }

    private readonly Shard[] _shards;
    private readonly ConcurrentDictionary<DecisionKey, Lazy<TableAccessDecision>> _inFlight = new();
    private int _waiters;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;

    public CasbinDecisionCache(int capacity, TimeSpan ttl, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(time);

        _ttl = ttl;
        _time = time;

        var shardCount = Math.Clamp(capacity / MinEntriesPerShard, 1, MaxShards);
        _shards = new Shard[shardCount];
        var perShard = capacity / shardCount;
        var remainder = capacity % shardCount;
        for (var i = 0; i < shardCount; i++)
        {
            _shards[i] = new Shard(perShard + (i < remainder ? 1 : 0));
        }
    }

    internal int ShardCount => _shards.Length;

    /// <summary>Callers currently inside <see cref="GetOrCompute"/> (leader included); a test seam.</summary>
    internal int DiagnosticWaiters => Volatile.Read(ref _waiters);

    public int Count
    {
        get
        {
            var total = 0;
            foreach (var shard in _shards)
            {
                lock (shard.Gate)
                {
                    total += shard.Map.Count;
                }
            }

            return total;
        }
    }

    private Shard ShardFor(in DecisionKey key) => _shards[(key.GetHashCode() & int.MaxValue) % _shards.Length];

    public bool TryGet(in DecisionKey key, out TableAccessDecision decision)
    {
        var shard = ShardFor(key);
        lock (shard.Gate)
        {
            if (shard.Map.TryGetValue(key, out var node))
            {
                if (_time.GetElapsedTime(node.Value.CreatedTimestamp) <= _ttl)
                {
                    shard.Lru.Remove(node);
                    shard.Lru.AddFirst(node);
                    decision = node.Value.Decision;
                    return true;
                }

                shard.Lru.Remove(node);
                shard.Map.Remove(key);
            }
        }

        decision = default!;
        return false;
    }

    public void Set(in DecisionKey key, TableAccessDecision decision)
    {
        var shard = ShardFor(key);
        lock (shard.Gate)
        {
            if (shard.Map.TryGetValue(key, out var existing))
            {
                shard.Lru.Remove(existing);
                shard.Map.Remove(key);
            }
            else if (shard.Map.Count >= shard.Capacity && shard.Lru.Last is { } tail)
            {
                shard.Lru.RemoveLast();
                shard.Map.Remove(tail.Value.Key);
            }

            var node = new LinkedListNode<Entry>(new Entry(key, decision, _time.GetTimestamp()));
            shard.Lru.AddFirst(node);
            shard.Map[key] = node;
        }
    }

    /// <summary>
    /// Single-flight miss handling: concurrent misses for the same key share one computation. The leader stores the decision when
    /// <paramref name="compute"/> says so; a computation that throws is not cached and the exception reaches every waiter (fail closed).
    /// </summary>
    public TableAccessDecision GetOrCompute(in DecisionKey key, Func<(TableAccessDecision Decision, bool Store)> compute)
    {
        ArgumentNullException.ThrowIfNull(compute);
        var captured = key;
        var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<TableAccessDecision>(() =>
        {
            var (decision, store) = compute();
            if (store)
            {
                Set(captured, decision);
            }

            return decision;
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        Interlocked.Increment(ref _waiters);
        try
        {
            return lazy.Value;
        }
        finally
        {
            Interlocked.Decrement(ref _waiters);
            _inFlight.TryRemove(new KeyValuePair<DecisionKey, Lazy<TableAccessDecision>>(key, lazy));
        }
    }

    public void Clear()
    {
        foreach (var shard in _shards)
        {
            lock (shard.Gate)
            {
                shard.Map.Clear();
                shard.Lru.Clear();
            }
        }
    }
}
