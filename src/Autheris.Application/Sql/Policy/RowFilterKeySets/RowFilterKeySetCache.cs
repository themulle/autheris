namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

using Autheris.Application.Caching;
using Microsoft.Extensions.Logging;

public enum RowFilterKeySetCacheStatus
{
    /// <summary>A valid cached entry was served.</summary>
    Hit,

    /// <summary>The entry was loaded (or joined an in-flight load) for this call.</summary>
    Miss,

    /// <summary>No load slot was free: no entry, the site keeps <c>EXISTS</c> (<see cref="KeySetFallbackReason.LoadThrottled"/>); nothing is cached.</summary>
    Throttled
}

/// <param name="AgeSeconds">Age of a <see cref="RowFilterKeySetCacheStatus.Hit"/>; null otherwise.</param>
public sealed record RowFilterKeySetCacheResult(RowFilterKeySetCacheStatus Status, RowFilterKeySetEntry? Entry, int? AgeSeconds);

/// <summary>
/// Issue #107 (PLAN-GOV-107 A-5): the dedicated cache of <see cref="RowFilterKeySetEntry"/> over <see cref="SingleFlightKeySetCache{TValue}"/>.
/// <list type="bullet">
/// <item>Own size limit; the entry expires absolutely at <c>LoadedAt + duration</c>, never sliding, never relative to load completion (SEC-RF107-04 b).</item>
/// <item>On every hit the stored tenant and data source are compared ordinally and <c>now - LoadedAt &lt; currentDuration</c> must hold strictly
/// (<see cref="TimeProvider"/>); a mismatch is a miss, the entry is removed and one Warning without values is logged (SEC-RF107-02, -04 c).</item>
/// <item>A waiter of an in-flight load receives the new result or the failure, never a stale entry (SEC-RF107-04 d).</item>
/// <item>An entry larger than <c>SizeLimit / 8</c> is stored as a small overflow sentinel instead of keys (SEC-RF107-13 b).</item>
/// <item>At most <c>MaxConcurrentKeyQueries</c> loads run at once per node; a slot is taken only immediately, otherwise the call returns
/// <see cref="RowFilterKeySetCacheStatus.Throttled"/> without loading. Joining an in-flight load is never throttled (SEC-RF107-13 d).</item>
/// </list>
/// Failures are never cached and propagate to every waiter.
/// </summary>
public sealed class RowFilterKeySetCache : IDisposable
{
    private readonly SingleFlightKeySetCache<RowFilterKeySetEntry> _inner;
    private readonly SemaphoreSlim _slots;
    private readonly TimeProvider _time;
    private readonly ILogger<RowFilterKeySetCache>? _logger;
    private readonly long _maxEntryBytes;

    public RowFilterKeySetCache(long sizeLimitBytes, int maxConcurrentKeyQueries, TimeProvider? timeProvider = null, ILogger<RowFilterKeySetCache>? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeLimitBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentKeyQueries);
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _inner = new SingleFlightKeySetCache<RowFilterKeySetEntry>(sizeLimitBytes, _time);
        _slots = new SemaphoreSlim(maxConcurrentKeyQueries, maxConcurrentKeyQueries);
        _maxEntryBytes = Math.Max(1, sizeLimitBytes / 8);
    }

    /// <summary>Estimated bytes of all cached entries.</summary>
    public long CurrentEstimatedSize => _inner.CurrentEstimatedSize;

    /// <param name="cacheKey"><see cref="RowFilterKeySetCacheKey.Compute"/>.</param>
    /// <param name="tenant">Verified against the stored entry on every hit and against a loaded entry.</param>
    /// <param name="duration">The current effective duration (the settings are resolved per statement, so a lowered value applies at once).</param>
    /// <param name="load">Runs at most once at a time per key; it must set <see cref="RowFilterKeySetEntry.LoadedAt"/> before the key query starts.</param>
    public async ValueTask<RowFilterKeySetCacheResult> GetOrLoadAsync(
        string cacheKey,
        string tenant,
        string dataSource,
        TimeSpan duration,
        Func<CancellationToken, Task<RowFilterKeySetEntry>> load,
        TimeSpan loadTimeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        var now = _time.GetUtcNow();
        if (_inner.TryGet(cacheKey, out var cached) && cached != null)
        {
            if (!Matches(cached, tenant, dataSource))
            {
                // SEC-RF107-02: never serve it, never log the values.
                _logger?.LogWarning("A cached row-filter key set did not match the tenant or data source of the request and was discarded.");
                _inner.Remove(cacheKey);
            }
            else if (IsFresh(cached, now, duration))
            {
                return new RowFilterKeySetCacheResult(RowFilterKeySetCacheStatus.Hit, cached, AgeSeconds(cached, now));
            }
            else
            {
                _inner.Remove(cacheKey);
            }
        }

        try
        {
            var entry = await _inner.GetOrLoadAsync(
                cacheKey,
                loadCt => LoadAsync(tenant, dataSource, duration, load, loadCt),
                ct,
                loadTimeout,
                accept: e => Matches(e, tenant, dataSource) && IsFresh(e, _time.GetUtcNow(), duration)).ConfigureAwait(false);
            return new RowFilterKeySetCacheResult(RowFilterKeySetCacheStatus.Miss, entry, null);
        }
        catch (LoadThrottledException)
        {
            return new RowFilterKeySetCacheResult(RowFilterKeySetCacheStatus.Throttled, null, null);
        }
    }

    public void Dispose()
    {
        _inner.Dispose();
        _slots.Dispose();
    }

    /// <summary>Test hook: stores an entry as if a load had produced it (forced-collision test, SEC-RF107-02).</summary>
    internal void InsertForTest(string cacheKey, RowFilterKeySetEntry entry, TimeSpan lifetime)
        => _inner.InsertForTest(cacheKey, entry, _time.GetUtcNow() + lifetime, entry.EstimateBytes());

    internal RowFilterKeySetEntry? TryGetRaw(string cacheKey) => _inner.TryGet(cacheKey, out var entry) ? entry : null;

    private async Task<SingleFlightLoadResult<RowFilterKeySetEntry>> LoadAsync(
        string tenant, string dataSource, TimeSpan duration, Func<CancellationToken, Task<RowFilterKeySetEntry>> load, CancellationToken ct)
    {
        // The slot is taken immediately or not at all: a throttled statement keeps EXISTS instead of queueing on a source (SEC-RF107-13 d).
        if (!_slots.Wait(0, CancellationToken.None))
        {
            throw new LoadThrottledException();
        }

        try
        {
            var entry = await load(ct).ConfigureAwait(false);
            if (!Matches(entry, tenant, dataSource))
            {
                throw new InvalidOperationException("The key set loader returned an entry for another tenant or data source.");   // fail closed, nothing cached
            }

            if (!entry.Overflow && !entry.NotBindable && entry.EstimateBytes() > _maxEntryBytes)
            {
                entry = RowFilterKeySetEntry.OverflowSentinel(entry.LoadedAt, entry.Tenant, entry.DataSource);
            }

            return new SingleFlightLoadResult<RowFilterKeySetEntry>(entry, entry.LoadedAt + duration, entry.EstimateBytes());
        }
        finally
        {
            _slots.Release();
        }
    }

    private static bool Matches(RowFilterKeySetEntry entry, string tenant, string dataSource)
        => string.Equals(entry.Tenant, tenant, StringComparison.Ordinal) && string.Equals(entry.DataSource, dataSource, StringComparison.Ordinal);

    private static bool IsFresh(RowFilterKeySetEntry entry, DateTimeOffset now, TimeSpan duration) => now - entry.LoadedAt < duration;

    private static int AgeSeconds(RowFilterKeySetEntry entry, DateTimeOffset now) => (int)Math.Max(0, (now - entry.LoadedAt).TotalSeconds);

    private sealed class LoadThrottledException : Exception;
}
