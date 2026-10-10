namespace Autheris.Application.VirtualFilters.Federation;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

/// <summary>
/// F-GOV-14 (SEC-10): dedicated, size-limited cache of authorized key sets. Separate from the shared
/// <see cref="IMemoryCache"/> so large key sets cannot evict access profiles; every entry carries its estimated size.
/// Loads are single-flight per cache key (CR-07): concurrent cold calls of one key share one in-flight load, and a slow
/// key source of one filter or tenant never delays the load of another cache key. A failed load is seen by its waiters
/// and never cached. The shared load is not tied to the first caller's cancellation (each caller only stops waiting);
/// it is bounded by its own timeout.
/// </summary>
public sealed class VirtualFilterKeySetCache : IDisposable
{
    private readonly MemoryCache _cache;
    private readonly ConcurrentDictionary<string, InFlightLoad> _inFlight = new(StringComparer.Ordinal);

    public VirtualFilterKeySetCache(IOptions<GatewayOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var federation = options.Value.VirtualFilters?.Federation ?? new VirtualFilterFederationOptions();
        federation.Validate();
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = federation.KeyCacheSizeLimitBytes, TrackStatistics = true });
    }

    /// <summary>Estimated bytes of all cached key sets.</summary>
    public long CurrentEstimatedSize => _cache.GetCurrentStatistics()?.CurrentEstimatedSize ?? 0;

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
        if (_cache.TryGetValue(key, out VirtualFilterKeySet? cached) && cached != null)
        {
            return cached with { CacheHit = true };
        }

        ct.ThrowIfCancellationRequested();
        var created = new InFlightLoad();
        var flight = _inFlight.GetOrAdd(key, created);
        if (ReferenceEquals(flight, created))
        {
            // Started only after the entry is visible, so its removal below can never run before its insertion.
            _ = Task.Run(() => RunAsync(key, created, ttl, load, loadTimeout), CancellationToken.None);
        }

        return await flight.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() => _cache.Dispose();

    private async Task RunAsync(
        string key, InFlightLoad flight, TimeSpan ttl, Func<CancellationToken, Task<VirtualFilterKeySet>> load, TimeSpan? timeout)
    {
        try
        {
            if (_cache.TryGetValue(key, out VirtualFilterKeySet? raced) && raced != null)
            {
                flight.Completion.SetResult(raced with { CacheHit = true });
                return;
            }

            using var cts = timeout is { } limit ? new CancellationTokenSource(limit) : new CancellationTokenSource();
            var loaded = await load(cts.Token).ConfigureAwait(false);
            _cache.Set(key, loaded, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl,
                Size = Math.Max(1, loaded.EstimateBytes())
            });
            flight.Completion.SetResult(loaded);
        }
        catch (Exception ex)
        {
            flight.Completion.SetException(ex);   // the waiters see the failure; nothing is cached
        }
        finally
        {
            ((ICollection<KeyValuePair<string, InFlightLoad>>)_inFlight).Remove(new KeyValuePair<string, InFlightLoad>(key, flight));
        }
    }

    /// <summary>One running load; it removes itself when it completes (success or failure), so a failure is never cached.</summary>
    private sealed class InFlightLoad
    {
        public TaskCompletionSource<VirtualFilterKeySet> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public InFlightLoad()
        {
            // A failure nobody waits for any more (all callers cancelled) must not surface as an unobserved task exception.
            _ = Completion.Task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }
}
