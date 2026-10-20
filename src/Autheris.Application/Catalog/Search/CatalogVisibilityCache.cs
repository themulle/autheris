using System.Collections.Concurrent;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

namespace Autheris.Application.Catalog.Search;

/// <summary>
/// Cache key for visible table sets under multi-epoch governance invalidation.
/// </summary>
public readonly record struct CatalogVisibilityCacheKey(
    string SubjectSid,
    string? DomainFilter,
    long StructureVersion,
    long ContentVersion,
    long PolicyEpoch,
    long RebacGeneration);

/// <summary>
/// Cached entry representing a visible bitset evaluated for a specific caller.
/// </summary>
public sealed record CachedVisibleSetEntry(
    CatalogVisibleBitSet BitSet,
    DateTimeOffset CachedAt);

/// <summary>
/// Multi-epoch visibility cache for catalog search.
/// </summary>
public interface ICatalogVisibilityCache
{
    bool TryGet(CatalogVisibilityCacheKey key, out CatalogVisibleBitSet? bitSet);
    void Set(CatalogVisibilityCacheKey key, CatalogVisibleBitSet bitSet);
    void InvalidateAll();
}

/// <summary>
/// Thread-safe bounded LRU-like cache for caller visibility bitsets.
/// </summary>
public sealed class CatalogVisibilityLruCache : ICatalogVisibilityCache
{
    private const int DefaultMaxEntries = 10_000;
    private readonly int _maxEntries;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<CatalogVisibilityCacheKey, CachedVisibleSetEntry> _cache;
    private readonly ILogger<CatalogVisibilityLruCache>? _logger;

    public int Count => _cache.Count;

    public CatalogVisibilityLruCache(
        TimeSpan? ttl = null,
        int maxEntries = DefaultMaxEntries,
        ILogger<CatalogVisibilityLruCache>? logger = null)
    {
        _ttl = ttl ?? TimeSpan.FromSeconds(10);
        _maxEntries = maxEntries;
        _logger = logger;
        _cache = new ConcurrentDictionary<CatalogVisibilityCacheKey, CachedVisibleSetEntry>();
    }

    public bool TryGet(CatalogVisibilityCacheKey key, out CatalogVisibleBitSet? bitSet)
    {
        bitSet = null;
        if (_cache.TryGetValue(key, out var entry))
        {
            if (DateTimeOffset.UtcNow - entry.CachedAt <= _ttl)
            {
                bitSet = entry.BitSet;
                return true;
            }

            // Expired entry
            _cache.TryRemove(key, out _);
        }

        return false;
    }

    public void Set(CatalogVisibilityCacheKey key, CatalogVisibleBitSet bitSet)
    {
        ArgumentNullException.ThrowIfNull(bitSet);

        // Bounded capacity guard: prune oldest if capacity exceeded
        if (_cache.Count >= _maxEntries)
        {
            PruneStaleOrOldest();
        }

        var entry = new CachedVisibleSetEntry(bitSet, DateTimeOffset.UtcNow);
        _cache[key] = entry;
    }

    public void InvalidateAll()
    {
        _cache.Clear();
    }

    private void PruneStaleOrOldest()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredKeys = _cache
            .Where(kvp => now - kvp.Value.CachedAt > _ttl)
            .Select(kvp => kvp.Key)
            .Take(100)
            .ToList();

        foreach (var k in expiredKeys)
        {
            _cache.TryRemove(k, out _);
        }

        if (_cache.Count >= _maxEntries)
        {
            // Drop 10% oldest
            var oldest = _cache
                .OrderBy(kvp => kvp.Value.CachedAt)
                .Select(kvp => kvp.Key)
                .Take(_maxEntries / 10)
                .ToList();

            foreach (var k in oldest)
            {
                _cache.TryRemove(k, out _);
            }
        }
    }
}
