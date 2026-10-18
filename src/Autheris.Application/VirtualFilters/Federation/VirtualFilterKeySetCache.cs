namespace Autheris.Application.VirtualFilters.Federation;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching;
using Microsoft.Extensions.Options;

/// <summary>
/// F-GOV-14 (SEC-10): dedicated, size-limited cache of authorized key sets. Separate from the shared
/// <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/> so large key sets cannot evict access profiles; every entry carries its estimated size.
/// Loads are single-flight per cache key (CR-07): concurrent cold calls of one key share one in-flight load, and a slow
/// key source of one filter or tenant never delays the load of another cache key. A failed load is seen by its waiters
/// and never cached. The shared load is not tied to the first caller's cancellation (each caller only stops waiting);
/// it is bounded by its own timeout. The mechanism is the generic <see cref="SingleFlightKeySetCache{TValue}"/> (issue #107).
/// </summary>
public sealed class VirtualFilterKeySetCache : IDisposable
{
    private readonly SingleFlightKeySetCache<VirtualFilterKeySet> _cache;

    public VirtualFilterKeySetCache(IOptions<GatewayOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var federation = options.Value.VirtualFilters?.Federation ?? new VirtualFilterFederationOptions();
        federation.Validate();
        _cache = new SingleFlightKeySetCache<VirtualFilterKeySet>(federation.KeyCacheSizeLimitBytes);
    }

    /// <summary>Estimated bytes of all cached key sets.</summary>
    public long CurrentEstimatedSize => _cache.CurrentEstimatedSize;

    /// <summary>
    /// The cached key set (with <see cref="VirtualFilterKeySet.CacheHit"/> set), or the result of <paramref name="load"/>,
    /// which runs at most once at a time per cache key. <paramref name="loadTimeout"/> bounds the shared load.
    /// </summary>
    internal async ValueTask<VirtualFilterKeySet> GetOrLoadAsync(
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<VirtualFilterKeySet>> load,
        CancellationToken ct,
        TimeSpan? loadTimeout = null)
    {
        if (_cache.TryGet(key, out var cached) && cached != null)
        {
            return cached with { CacheHit = true };
        }

        return await _cache.GetOrLoadAsync(
            key,
            async loadCt =>
            {
                var loaded = await load(loadCt).ConfigureAwait(false);
                return new SingleFlightLoadResult<VirtualFilterKeySet>(loaded, DateTimeOffset.UtcNow + ttl, Math.Max(1, loaded.EstimateBytes()));
            },
            ct,
            loadTimeout).ConfigureAwait(false);
    }

    public void Dispose() => _cache.Dispose();
}
