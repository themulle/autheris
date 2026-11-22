namespace Autheris.Application.State.Caching.TableCache;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Options.TableCache;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed record TableCacheLoadResult(
    long RowCount,
    long SizeInBytes,
    IReadOnlyList<string> Columns,
    IAsyncEnumerable<IReadOnlyList<object?>> Rows);

public interface ITableCacheUpstreamLoader
{
    Task<TableCacheLoadResult> LoadTableDataAsync(TableIdentifier table, CancellationToken ct = default);
}

/// <summary>
/// Orchestrates table cache life-cycle, single-flight request coalescing, and quota management.
/// Conforms to 10, and 03.
/// </summary>
public sealed class TableCacheManager : ITableCacheManager
{
    private readonly ConcurrentDictionary<TableIdentifier, InFlightLoad> _inFlightLoads = new();
    private readonly ConcurrentDictionary<TableIdentifier, ProactiveRefreshState> _proactiveStates = new();
    private readonly ITableCacheCatalog _catalog;
    private readonly ITableCacheStore _store;
    private readonly ITableCacheUpstreamLoader? _upstreamLoader;
    private readonly TableCacheOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TableCacheManager>? _logger;

    private long _totalHits;
    private long _totalMisses;
    private long _totalEvictions;
    private long _totalRefreshes;
    private long _totalStaleServes;

    public TableCacheManager(
        ITableCacheCatalog catalog,
        ITableCacheStore store,
        IOptions<TableCacheOptions> options,
        ITableCacheUpstreamLoader? upstreamLoader = null,
        TimeProvider? timeProvider = null,
        ILogger<TableCacheManager>? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _upstreamLoader = upstreamLoader;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public async ValueTask<TableCacheEntryMetadata?> GetOrLoadAsync(TableIdentifier table, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();

        if (_catalog.TryGet(table, out var existing) && existing is not null)
        {
            // Check if fresh and valid
            if (existing.IsHealthy && (existing.ExpiresAt == null || existing.ExpiresAt > now))
            {
                Interlocked.Increment(ref _totalHits);
                CheckProactiveRefresh(table, existing, now);
                return existing;
            }

            // If expired or unhealthy, check offline policy
            if (existing.OfflinePolicy == TableCacheOfflinePolicy.ServeStaleWithWarning)
            {
                var staleDuration = now - existing.LastRefreshedAt;
                var maxStale = TimeSpan.FromSeconds(_options.MaxStaleSurvivalSeconds);

                // 10 Max Stale Survival Boundary Ceiling
                if (staleDuration > maxStale)
                {
                    _logger?.LogWarning("SEC-BL04-10: Stale survival ceiling exceeded for table '{Table}' ({StaleDuration} > {MaxStale}). Fails closed with 503.", table, staleDuration, maxStale);
                    throw new InvalidOperationException($"GATEWAY_CACHE_STALE_EXPIRED: Table '{table}' exceeded maximum stale survival boundary of {maxStale.TotalSeconds} seconds.");
                }

                // If upstream loader is not provided or fails, serve stale
                if (_upstreamLoader == null)
                {
                    Interlocked.Increment(ref _totalStaleServes);
                    return existing;
                }
            }
        }

        Interlocked.Increment(ref _totalMisses);

        // 12 Single-Flight Request Coalescing
        return await CoalescedLoadAsync(table, allowStaleFallback: true, now, ct).ConfigureAwait(false);
    }

    public async Task ForceRefreshAsync(TableIdentifier table, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await CoalescedLoadAsync(table, allowStaleFallback: false, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
    }

    private async Task<TableCacheEntryMetadata> CoalescedLoadAsync(
        TableIdentifier table,
        bool allowStaleFallback,
        DateTimeOffset now,
        CancellationToken ct)
    {
        while (true)
        {
            if (_inFlightLoads.TryGetValue(table, out var existingFlight) && existingFlight.TryAcquireWaiter())
            {
                return await AwaitFlightAsync(existingFlight, ct).ConfigureAwait(false);
            }

            var newFlight = new InFlightLoad();
            if (_inFlightLoads.TryAdd(table, newFlight))
            {
                _ = RunSingleFlightLoadAsync(table, newFlight, allowStaleFallback, now);
                return await AwaitFlightAsync(newFlight, ct).ConfigureAwait(false);
            }

            newFlight.MarkCompletedAndDispose();
        }
    }

    private static async Task<TableCacheEntryMetadata> AwaitFlightAsync(InFlightLoad flight, CancellationToken ct)
    {
        try
        {
            return await flight.Tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            flight.ReleaseWaiter();
        }
    }

    private async Task RunSingleFlightLoadAsync(
        TableIdentifier table,
        InFlightLoad flight,
        bool allowStaleFallback,
        DateTimeOffset now)
    {
        try
        {
            var freshMetadata = await ExecuteUpstreamLoadAsync(table, flight.Cts.Token).ConfigureAwait(false);
            _catalog.Upsert(freshMetadata);
            Interlocked.Increment(ref _totalRefreshes);
            flight.Tcs.TrySetResult(freshMetadata);
        }
        catch (OperationCanceledException oce) when (flight.Cts.IsCancellationRequested)
        {
            flight.Tcs.TrySetCanceled(oce.CancellationToken.IsCancellationRequested ? oce.CancellationToken : flight.Cts.Token);
        }
        catch (Exception ex)
        {
            if (allowStaleFallback &&
                _catalog.TryGet(table, out var stale) &&
                stale is not null &&
                stale.OfflinePolicy == TableCacheOfflinePolicy.ServeStaleWithWarning)
            {
                var staleDuration = now - stale.LastRefreshedAt;
                var maxStale = TimeSpan.FromSeconds(_options.MaxStaleSurvivalSeconds);
                if (staleDuration <= maxStale)
                {
                    Interlocked.Increment(ref _totalStaleServes);
                    flight.Tcs.TrySetResult(stale);
                    return;
                }
            }

            flight.Tcs.TrySetException(ex);
        }
        finally
        {
            _inFlightLoads.TryRemove(new KeyValuePair<TableIdentifier, InFlightLoad>(table, flight));
            flight.MarkCompletedAndDispose();
        }
    }

    public async Task EvictExpiredAsync(CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        var all = _catalog.GetAll();

        foreach (var meta in all)
        {
            if (meta.ExpiresAt.HasValue && meta.ExpiresAt.Value <= now)
            {
                if (meta.OfflinePolicy != TableCacheOfflinePolicy.ServeStaleWithWarning)
                {
                    _catalog.Remove(meta.Table);
                    await _store.EvictAsync(meta.Table, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _totalEvictions);
                }
            }
        }
    }

    public TableCacheStats GetStats()
    {
        var snapshot = _catalog.GetSnapshot();
        long memBytes = 0;
        long diskBytes = 0;

        foreach (var entry in snapshot.Values)
        {
            if (entry.StorageTier == TableCacheStorageTier.Memory)
            {
                memBytes += entry.SizeInBytes;
            }
            else
            {
                diskBytes += entry.SizeInBytes;
            }
        }

        return new TableCacheStats(
            TotalHits: Interlocked.Read(ref _totalHits),
            TotalMisses: Interlocked.Read(ref _totalMisses),
            TotalEvictions: Interlocked.Read(ref _totalEvictions),
            TotalRefreshes: Interlocked.Read(ref _totalRefreshes),
            TotalStaleServes: Interlocked.Read(ref _totalStaleServes),
            TotalMemoryBytes: memBytes,
            TotalDiskBytes: diskBytes,
            ActiveTableCount: snapshot.Count);
    }

    private void CheckProactiveRefresh(TableIdentifier table, TableCacheEntryMetadata existing, DateTimeOffset now)
    {
        if (existing.ExpiresAt == null) return;

        var targetOpt = _options.Tables.FirstOrDefault(t =>
            string.Equals(t.Table, table.TableName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.Table, $"{table.Schema}.{table.TableName}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.Table, table.ToString(), StringComparison.OrdinalIgnoreCase));

        var ratio = targetOpt?.ProactiveEarlyRefreshRatio ?? 0.10;
        var totalTtl = targetOpt?.TtlSeconds.HasValue == true
            ? TimeSpan.FromSeconds(targetOpt.TtlSeconds.Value)
            : TimeSpan.FromSeconds(_options.DefaultTtlSeconds);

        var remaining = existing.ExpiresAt.Value - now;
        if (remaining > TimeSpan.Zero && remaining < totalTtl * ratio && _upstreamLoader != null && !_inFlightLoads.ContainsKey(table))
        {
            var state = _proactiveStates.GetOrAdd(table, static _ => new ProactiveRefreshState());
            if (!state.TryBegin(now))
            {
                return;
            }

            // Trigger deduplicated proactive background reload with failure backoff
            _ = Task.Run(async () =>
            {
                try
                {
                    await ForceRefreshAsync(table, CancellationToken.None).ConfigureAwait(false);
                    state.RecordSuccess();
                }
                catch (Exception ex)
                {
                    state.RecordFailure(_timeProvider.GetUtcNow());
                    _logger?.LogWarning(ex, "Proactive early refresh failed for table '{Table}'", table);
                }
            });
        }
    }

    private async Task<TableCacheEntryMetadata> ExecuteUpstreamLoadAsync(TableIdentifier table, CancellationToken ct)
    {
        if (_upstreamLoader == null)
        {
            throw new InvalidOperationException($"No upstream loader registered to refresh table '{table}'.");
        }

        var loadResult = await _upstreamLoader.LoadTableDataAsync(table, ct).ConfigureAwait(false);
        var targetOpt = _options.Tables.FirstOrDefault(t =>
            string.Equals(t.Table, table.TableName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.Table, $"{table.Schema}.{table.TableName}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.Table, table.ToString(), StringComparison.OrdinalIgnoreCase));

        long epoch = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        int ttlSeconds = targetOpt?.TtlSeconds ?? _options.DefaultTtlSeconds;
        var expiresAt = _timeProvider.GetUtcNow().AddSeconds(ttlSeconds);

        var shadowIdentifier = await _store.StageShadowGenerationAsync(table, epoch, loadResult.Columns, loadResult.Rows, ct).ConfigureAwait(false);
        var meta = await _store.SwapActiveGenerationAsync(table, epoch, shadowIdentifier, loadResult.RowCount, loadResult.SizeInBytes, expiresAt, ct).ConfigureAwait(false);

        // Update metadata with cached columns and explicit or default policy
        var enrichedMeta = meta with
        {
            CachedColumns = loadResult.Columns,
            FilterPredicate = targetOpt?.FilterPredicate,
            OfflinePolicy = targetOpt?.OfflinePolicy ?? TableCacheOfflinePolicy.FailClosed,
            PrimaryKeyColumns = targetOpt?.PrimaryKeyColumns ?? meta.PrimaryKeyColumns
        };

        return enrichedMeta;
    }

    private sealed class InFlightLoad
    {
        private readonly object _sync = new();
        private int _waiters = 1;
        private bool _completed;
        private bool _disposed;

        public CancellationTokenSource Cts { get; } = new();
        public TaskCompletionSource<TableCacheEntryMetadata> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryAcquireWaiter()
        {
            lock (_sync)
            {
                if (_completed || _disposed || _waiters <= 0)
                {
                    return false;
                }

                _waiters++;
                return true;
            }
        }

        public void ReleaseWaiter()
        {
            lock (_sync)
            {
                if (--_waiters <= 0 && !_completed && !_disposed)
                {
                    Cts.Cancel();
                }
            }
        }

        public void MarkCompletedAndDispose()
        {
            lock (_sync)
            {
                _completed = true;
                if (!_disposed)
                {
                    _disposed = true;
                    Cts.Dispose();
                }
            }
        }
    }

    private sealed class ProactiveRefreshState
    {
        private readonly object _sync = new();
        private bool _inFlight;
        private int _consecutiveFailures;
        private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;

        public bool TryBegin(DateTimeOffset now)
        {
            lock (_sync)
            {
                if (_inFlight || now < _backoffUntil)
                {
                    return false;
                }

                _inFlight = true;
                return true;
            }
        }

        public void RecordSuccess()
        {
            lock (_sync)
            {
                _inFlight = false;
                _consecutiveFailures = 0;
                _backoffUntil = DateTimeOffset.MinValue;
            }
        }

        public void RecordFailure(DateTimeOffset now)
        {
            lock (_sync)
            {
                _inFlight = false;
                _consecutiveFailures++;
                var backoffSeconds = Math.Min(60, 5 * (1 << Math.Min(_consecutiveFailures - 1, 4)));
                _backoffUntil = now.AddSeconds(backoffSeconds);
            }
        }
    }
}
