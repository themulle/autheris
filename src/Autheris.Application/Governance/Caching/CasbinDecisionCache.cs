namespace Autheris.Application.Governance.Caching;

using System;
using System.Collections.Generic;
using Autheris.Domain.Interfaces;

/// <summary>
/// Bounded LRU cache of policy decisions. All operations are O(1) under a single lock. At capacity only the least recently used
/// entry is evicted (never a global flush); entries older than the TTL are dropped on access. Time comes from the injected
/// <see cref="TimeProvider"/> so expiry is deterministic in tests.
/// </summary>
internal sealed class CasbinDecisionCache
{
    private sealed class Entry(DecisionKey key, TableAccessDecision decision, long createdTimestamp)
    {
        public DecisionKey Key { get; } = key;

        public TableAccessDecision Decision { get; } = decision;

        public long CreatedTimestamp { get; } = createdTimestamp;
    }

    private readonly object _gate = new();
    private readonly Dictionary<DecisionKey, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _lru = new();
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;

    public CasbinDecisionCache(int capacity, TimeSpan ttl, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(time);

        _capacity = capacity;
        _ttl = ttl;
        _time = time;
        _map = new Dictionary<DecisionKey, LinkedListNode<Entry>>(Math.Min(capacity, 1024));
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    public bool TryGet(in DecisionKey key, out TableAccessDecision decision)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                if (_time.GetElapsedTime(node.Value.CreatedTimestamp) <= _ttl)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    decision = node.Value.Decision;
                    return true;
                }

                _lru.Remove(node);
                _map.Remove(key);
            }
        }

        decision = default!;
        return false;
    }

    public void Set(in DecisionKey key, TableAccessDecision decision)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _map.Remove(key);
            }
            else if (_map.Count >= _capacity && _lru.Last is { } tail)
            {
                _lru.RemoveLast();
                _map.Remove(tail.Value.Key);
            }

            var node = new LinkedListNode<Entry>(new Entry(key, decision, _time.GetTimestamp()));
            _lru.AddFirst(node);
            _map[key] = node;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _lru.Clear();
        }
    }
}
