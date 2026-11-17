namespace Autheris.Application.State.Caching;

using System;
using System.Collections.Generic;

/// <summary>
/// <see cref="BoundedCache{TKey,TValue}"/> with a per-tenant share cap: eviction is global LRU, but no tenant may hold more than
/// half of the capacity. Inserting a new key for a tenant that is at its cap first evicts that tenant's oldest-written entry, so one
/// tenant can never push every other tenant's entries out. Eviction only produces a miss that callers recompute from the authoritative source.
/// Cache values must carry no more tenant-specific data than the key partition implies; a miss never relaxes a check.
/// </summary>
public sealed class TenantShareCappedCache<TKey, TValue>
    where TKey : notnull
{
    private readonly BoundedCache<TKey, TValue> _cache;
    private readonly Func<TKey, string> _tenantOf;
    private readonly int _capacity;
    private readonly int _perTenantCap;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedList<TKey>> _byTenant = new(StringComparer.Ordinal);
    private readonly Dictionary<TKey, LinkedListNode<TKey>> _nodes;

    public TenantShareCappedCache(int capacity, Func<TKey, string> tenantOf, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(tenantOf);
        _cache = new BoundedCache<TKey, TValue>(capacity, comparer: comparer);
        _tenantOf = tenantOf;
        _capacity = capacity;
        _perTenantCap = Math.Max(1, capacity / 2);
        _nodes = new Dictionary<TKey, LinkedListNode<TKey>>(comparer);
    }

    /// <summary>Maximum number of entries one tenant may hold.</summary>
    public int PerTenantCap => _perTenantCap;

    public int Count => _cache.Count;

    public bool TryGet(TKey key, out TValue value) => _cache.TryGet(key, out value);

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_nodes.TryGetValue(key, out var existing))
            {
                existing.List!.Remove(existing);
                existing.List.AddLast(existing);
            }
            else
            {
                var tenant = _tenantOf(key);
                if (!_byTenant.TryGetValue(tenant, out var list))
                {
                    list = new LinkedList<TKey>();
                    _byTenant[tenant] = list;
                }

                while (list.Count >= _perTenantCap && list.First is { } oldest)
                {
                    list.RemoveFirst();
                    _nodes.Remove(oldest.Value);
                    _cache.TryRemove(oldest.Value);
                }

                _nodes[key] = list.AddLast(key);
            }

            _cache.Set(key, value);
            if (_nodes.Count > 2 * _capacity)
            {
                PruneEvicted();
            }
        }
    }

    public bool TryRemove(TKey key)
    {
        lock (_gate)
        {
            Untrack(key);
            return _cache.TryRemove(key);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _nodes.Clear();
            _byTenant.Clear();
            _cache.Clear();
        }
    }

    // Entries the global LRU already dropped stay tracked until here; the sweep keeps the bookkeeping within a constant factor of the capacity.
    private void PruneEvicted()
    {
        var live = new HashSet<TKey>(_cache.Keys, _nodes.Comparer);
        foreach (var key in new List<TKey>(_nodes.Keys))
        {
            if (!live.Contains(key))
            {
                Untrack(key);
            }
        }
    }

    private void Untrack(TKey key)
    {
        if (_nodes.Remove(key, out var node) && node.List is { } list)
        {
            list.Remove(node);
            if (list.Count == 0)
            {
                _byTenant.Remove(_tenantOf(key));
            }
        }
    }
}
