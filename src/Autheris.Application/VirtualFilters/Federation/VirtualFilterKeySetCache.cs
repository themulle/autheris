namespace Autheris.Application.VirtualFilters.Federation;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

/// <summary>
/// F-GOV-14 (SEC-10): dedicated, size-limited cache of authorized key sets. Separate from the shared
/// <see cref="IMemoryCache"/> so large key sets cannot evict access profiles; every entry carries its estimated size.
/// Loads are single-flight per cache key (striped locks), failures are never cached.
/// </summary>
public sealed class VirtualFilterKeySetCache : IDisposable
{
    private const int LockStripes = 32;

    private readonly MemoryCache _cache;
    private readonly SemaphoreSlim[] _locks;

    public VirtualFilterKeySetCache(IOptions<GatewayOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var federation = options.Value.VirtualFilters?.Federation ?? new VirtualFilterFederationOptions();
        federation.Validate();
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = federation.KeyCacheSizeLimitBytes, TrackStatistics = true });
        _locks = new SemaphoreSlim[LockStripes];
        for (int i = 0; i < _locks.Length; i++)
        {
            _locks[i] = new SemaphoreSlim(1, 1);
        }
    }

    /// <summary>Estimated bytes of all cached key sets.</summary>
    public long CurrentEstimatedSize => _cache.GetCurrentStatistics()?.CurrentEstimatedSize ?? 0;

    /// <summary>
    /// The cached key set (with <see cref="VirtualFilterKeySet.CacheHit"/> set), or the result of <paramref name="load"/>,
    /// which runs at most once at a time per cache key.
    /// </summary>
    internal async ValueTask<VirtualFilterKeySet> GetOrLoadAsync(
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<VirtualFilterKeySet>> load,
        CancellationToken ct)
    {
        if (_cache.TryGetValue(key, out VirtualFilterKeySet? cached) && cached != null)
        {
            return cached with { CacheHit = true };
        }

        var gate = _locks[(int)((uint)StringComparer.Ordinal.GetHashCode(key) % LockStripes)];
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out cached) && cached != null)
            {
                return cached with { CacheHit = true };
            }

            var loaded = await load(ct).ConfigureAwait(false);
            _cache.Set(key, loaded, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl,
                Size = Math.Max(1, loaded.EstimateBytes())
            });
            return loaded;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        _cache.Dispose();
        foreach (var gate in _locks)
        {
            gate.Dispose();
        }
    }
}
