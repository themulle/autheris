namespace Autheris.Application.Caching;

using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;

/// <summary>A loaded value with its absolute expiration and estimated size in bytes.</summary>
public sealed record SingleFlightLoadResult<TValue>(TValue Value, DateTimeOffset AbsoluteExpiration, long SizeBytes) where TValue : class;

/// <summary>
/// Issue #107 (PLAN-GOV-107 A-5): generic dedicated, size-limited, single-flight cache extracted from <c>VirtualFilterKeySetCache</c>.
/// <list type="bullet">
/// <item>Single flight per key: concurrent cold callers of one key share one in-flight load; a slow load of one key never delays another.</item>
/// <item>A failed load is seen by its waiters and never cached.</item>
/// <item>The shared load is detached from caller cancellation (each caller only stops waiting) and bounded by its own timeout.</item>
/// <item>Absolute expiration only (never sliding), measured with the injected <see cref="TimeProvider"/>.</item>
/// <item>A waiter of an in-flight load receives the new result or the failure, never a stale value (SEC-RF107-04 d).</item>
/// </list>
/// </summary>
public sealed class SingleFlightKeySetCache<TValue> : IDisposable where TValue : class
{
    private readonly MemoryCache _cache;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, InFlightLoad> _inFlight = new(StringComparer.Ordinal);

    public SingleFlightKeySetCache(long sizeLimitBytes, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeLimitBytes);
        _time = timeProvider ?? TimeProvider.System;
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = sizeLimitBytes,
            TrackStatistics = true,
            Clock = new TimeProviderClock(_time)
        });
    }

    /// <summary>Estimated bytes of all cached values.</summary>
    public long CurrentEstimatedSize => _cache.GetCurrentStatistics()?.CurrentEstimatedSize ?? 0;

    public bool TryGet(string key, out TValue? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_cache.TryGetValue(key, out TValue? cached) && cached != null)
        {
            value = cached;
            return true;
        }

        value = null;
        return false;
    }

    public void Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _cache.Remove(key);
    }

    /// <summary>
    /// The cached value, or the result of <paramref name="load"/>, which runs at most once at a time per key. <paramref name="loadTimeout"/>
    /// bounds the shared load (<see cref="TimeoutException"/> for its waiters). <paramref name="accept"/> rejects a cached or raced entry the
    /// caller must not use (for example one older than the current duration); a rejected entry is replaced by a new load.
    /// </summary>
    public async ValueTask<TValue> GetOrLoadAsync(
        string key,
        Func<CancellationToken, Task<SingleFlightLoadResult<TValue>>> load,
        CancellationToken ct,
        TimeSpan? loadTimeout = null,
        Func<TValue, bool>? accept = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(load);
        if (TryGet(key, out var cached) && (accept is null || accept(cached!)))
        {
            return cached!;
        }

        ct.ThrowIfCancellationRequested();
        var created = new InFlightLoad();
        var flight = _inFlight.GetOrAdd(key, created);
        if (ReferenceEquals(flight, created))
        {
            // The timeout starts now (not on a pool thread), so it never depends on scheduling. The load starts only after its
            // flight is registered, so the flight's removal can never run before its registration.
            var cts = loadTimeout is { } limit ? new CancellationTokenSource(limit, _time) : new CancellationTokenSource();
            _ = Task.Run(() => RunAsync(key, created, load, cts, accept), CancellationToken.None);
        }

        return await flight.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() => _cache.Dispose();

    private async Task RunAsync(
        string key,
        InFlightLoad flight,
        Func<CancellationToken, Task<SingleFlightLoadResult<TValue>>> load,
        CancellationTokenSource cts,
        Func<TValue, bool>? accept)
    {
        try
        {
            if (TryGet(key, out var raced) && (accept is null || accept(raced!)))
            {
                flight.Completion.SetResult(raced!);
                return;
            }

            SingleFlightLoadResult<TValue> loaded;
            try
            {
                var task = load(cts.Token);
                _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                loaded = await task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException("The key set load exceeded its timeout.");
            }

            _cache.Set(key, loaded.Value, new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = loaded.AbsoluteExpiration,
                Size = Math.Max(1, loaded.SizeBytes)
            });
            flight.Completion.SetResult(loaded.Value);
        }
        catch (Exception ex)
        {
            flight.Completion.SetException(ex);   // the waiters see the failure; nothing is cached
        }
        finally
        {
            cts.Dispose();
            ((ICollection<KeyValuePair<string, InFlightLoad>>)_inFlight).Remove(new KeyValuePair<string, InFlightLoad>(key, flight));
        }
    }

    /// <summary>One running load; it removes itself when it completes (success or failure), so a failure is never cached.</summary>
    private sealed class InFlightLoad
    {
        public TaskCompletionSource<TValue> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public InFlightLoad()
        {
            // A failure nobody waits for any more (all callers cancelled) must not surface as an unobserved task exception.
            _ = Completion.Task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    private sealed class TimeProviderClock(TimeProvider time) : ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }
}
