using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;

namespace Autheris.GraphQL.Catalog;

// Issue #802: committed catalog writes (catalog:changed) start a debounced catalog check instead of waiting for the periodic
// tick, and the module reports whether the served schema is behind the catalog. Rebuilds still go through the #786 latch.
public sealed partial class CatalogGraphQlTypeModule : ICatalogSchemaFreshness
{
    /// <summary>Issue #802: delay of the check after the first change signal of a burst, so one request's writes land in one check.</summary>
    internal static readonly TimeSpan ChangeSignalDebounce = TimeSpan.FromSeconds(1);

    /// <summary>Issue #802: lower bound of the quiet period that ends a burst of change signals.</summary>
    internal static readonly TimeSpan MinQuietPeriod = TimeSpan.FromSeconds(2);

    /// <summary>Issue #802: upper bound of the quiet period (the default periodic refresh interval).</summary>
    internal static readonly TimeSpan MaxQuietPeriod = TimeSpan.FromSeconds(60);

    /// <summary>Issue #802: minimum spacing of the status endpoint's catalog version reads (caps store load per instance).</summary>
    internal static readonly TimeSpan StatusReadSpacing = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan StatusVersionReadTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan StatusStoreReadTimeout = TimeSpan.FromSeconds(5);

    private readonly Lock _signalLock = new();
    private readonly IDisposable? _changeSubscription;
    private ITimer? _signalTimer;
    private DateTimeOffset? _signalDue;
    private bool _signalDueIsLeading;
    private DateTimeOffset? _lastSignalAt;
    private DateTimeOffset _burstStart;
    private bool _signalDisposed;
    private long _signalSeq;
    private long _checkedSignalSeq;
    private long _lastBuildDurationTicks;
    private long _nextTickDueUtcTicks = long.MaxValue;
    private int _buildInFlight;

    private readonly Lock _statusReadLock = new();
    private TaskCompletionSource<CatalogVersion>? _queuedStatusRead;
    private long _lastStatusReadStart = NoTimestamp;

    /// <summary>
    /// Quiet period that ends a burst: the duration of the last executor build, clamped to [2 s, 60 s]. A large catalog
    /// (one build takes minutes) therefore settles no faster than the periodic tick did before, a small one within seconds.
    /// </summary>
    private TimeSpan QuietPeriod
    {
        get
        {
            var build = TimeSpan.FromTicks(Volatile.Read(ref _lastBuildDurationTicks));
            return build < MinQuietPeriod ? MinQuietPeriod : build > MaxQuietPeriod ? MaxQuietPeriod : build;
        }
    }

    /// <summary>True while an event-driven check is scheduled (tests).</summary>
    internal bool HasScheduledSignalCheck
    {
        get
        {
            lock (_signalLock)
            {
                return _signalDue.HasValue;
            }
        }
    }

    private IDisposable? SubscribeToCatalogChanges(IEventBus? eventBus) =>
        eventBus?.Subscribe<object>(
            ICatalogVersionProvider.CatalogChangedEventChannel,
            _ =>
            {
                // The payload is ignored (the reloadSchema mutation publishes 0:0); no work runs on the bus thread.
                SignalCatalogChanged();
                return Task.CompletedTask;
            });

    /// <summary>
    /// Records a committed catalog change. The first signal after a quiet period schedules a check after
    /// <see cref="ChangeSignalDebounce"/>; later signals of the same burst move the check to the end of the burst (quiet
    /// period after the last signal, at the latest <see cref="MaxRebuildDeferral"/> after the burst started).
    /// </summary>
    internal void SignalCatalogChanged()
    {
        Interlocked.Increment(ref _signalSeq);
        lock (_signalLock)
        {
            if (_signalDisposed)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            var quiet = QuietPeriod;
            var leading = _lastSignalAt is not { } last || now - last >= quiet;
            _lastSignalAt = now;
            if (leading)
            {
                _burstStart = now;
                var due = now + ChangeSignalDebounce;
                if (_signalDue is not { } armed || due < armed)
                {
                    ArmSignalTimerLocked(due, leading: true, now);
                }
            }
            else if (!(_signalDue.HasValue && _signalDueIsLeading))
            {
                // A pending leading-edge check is never pushed out; it runs after these writes anyway.
                ArmSignalTimerLocked(TrailingDueLocked(now, quiet), leading: false, now);
            }
        }
    }

    /// <summary>
    /// Issue #802: a check deferred the rebuild (burst); one follow-up check ends it after the quiet period, unless the
    /// periodic tick comes first (it ends the burst as well, so a second full check would only add load).
    /// </summary>
    private void ScheduleFollowUpCheck()
    {
        if (_changeSubscription is null)
        {
            return;
        }

        lock (_signalLock)
        {
            if (_signalDisposed || _signalDue.HasValue)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            if (now - _burstStart >= MaxRebuildDeferral)
            {
                // No signalled burst is running (the deferral came from a periodic tick): the cap counts from now.
                _burstStart = now;
            }

            var due = TrailingDueLocked(now, QuietPeriod);
            if (Volatile.Read(ref _nextTickDueUtcTicks) <= due.UtcTicks)
            {
                return;
            }

            ArmSignalTimerLocked(due, leading: false, now);
        }
    }

    private void RecordNextTick() =>
        Volatile.Write(ref _nextTickDueUtcTicks, (_timeProvider.GetUtcNow() + _refreshInterval).UtcTicks);

    private DateTimeOffset TrailingDueLocked(DateTimeOffset now, TimeSpan quiet)
    {
        var due = now + quiet;
        var cap = _burstStart + MaxRebuildDeferral;
        return due <= cap ? due : cap < now ? now : cap;
    }

    private void ArmSignalTimerLocked(DateTimeOffset due, bool leading, DateTimeOffset now)
    {
        _signalDue = due;
        _signalDueIsLeading = leading;
        _signalTimer ??= _timeProvider.CreateTimer(_ => OnSignalTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _signalTimer.Change(due - now, Timeout.InfiniteTimeSpan);
        _logger?.LogDebug("GraphQL catalog change signalled; check scheduled in {DelayMs} ms.", (long)(due - now).TotalMilliseconds);
    }

    private void OnSignalTimer()
    {
        lock (_signalLock)
        {
            if (_signalDisposed)
            {
                return;
            }

            _signalDue = null;
            _burstStart = _timeProvider.GetUtcNow();
        }

        _ = RunSignalledCheckAsync();
    }

    private async Task RunSignalledCheckAsync()
    {
        var seq = Volatile.Read(ref _signalSeq);
        try
        {
            // Before the startup build has started there is nothing to compare with; that build reads the current catalog.
            if (Volatile.Read(ref _everBuilt) != 0)
            {
                // forceFull: the version provider's cache is invalidated by the same asynchronous event and may still
                // return the old version. The candidate fingerprint decides, so a write without schema effect never rebuilds.
                await CheckForCatalogChangesAsync(fromTimer: false, forceFull: true).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "GraphQL catalog change check failed; the periodic check retries.");
        }
        finally
        {
            // Only after the check has stored its result (pending model, request, built version): the status reads in that order.
            RaiseToAtLeast(ref _checkedSignalSeq, seq);
        }
    }

    private static void RaiseToAtLeast(ref long target, long value)
    {
        var seen = Volatile.Read(ref target);
        while (seen < value)
        {
            var prior = Interlocked.CompareExchange(ref target, value, seen);
            if (prior == seen)
            {
                return;
            }

            seen = prior;
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsSchemaCurrentAsync(CancellationToken ct = default)
    {
        // Read in pipeline order (signal, pending model, request, build): every hand-over sets the next stage before it
        // clears the previous one, so a change in transit is always seen in at least one stage.
        if (Volatile.Read(ref _signalSeq) != Volatile.Read(ref _checkedSignalSeq) ||
            Volatile.Read(ref _pendingBuild) is not null ||
            Volatile.Read(ref _rebuildRequested) != 0 ||
            Volatile.Read(ref _everBuilt) == 0 ||
            Volatile.Read(ref _buildInFlight) != 0 ||
            _built.Fingerprint is null)
        {
            return false;
        }

        if (_versionProvider is null)
        {
            return true;
        }

        var builtStructure = Volatile.Read(ref _builtStructureVersion);
        var builtContent = Volatile.Read(ref _builtContentVersion);
        if (builtStructure < 0 || builtContent < 0)
        {
            return false;
        }

        // The change event may not have arrived yet (queued in-process delivery, Redis round trip, a dropped event, another
        // instance's write), so the store decides: a read that starts after this request arrived, uncached.
        CatalogVersion current;
        try
        {
            current = await ReadCatalogVersionForStatusAsync().WaitAsync(StatusVersionReadTimeout, _timeProvider, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail-closed: an unknown catalog version is never reported as current.
            _logger?.LogDebug(ex, "GraphQL schema status: the catalog version could not be read.");
            return false;
        }

        return current.StructureVersion <= builtStructure && current.ContentVersion <= builtContent;
    }

    /// <summary>
    /// Returns the first store read that starts after this call: callers join a read that is queued but not started, never
    /// one in flight (it may predate their write). Reads start at least <see cref="StatusReadSpacing"/> apart, which bounds
    /// the store load whatever the request rate.
    /// </summary>
    private Task<CatalogVersion> ReadCatalogVersionForStatusAsync()
    {
        TaskCompletionSource<CatalogVersion> read;
        TimeSpan wait;
        lock (_statusReadLock)
        {
            if (_queuedStatusRead is { } queued)
            {
                return queued.Task;
            }

            read = new TaskCompletionSource<CatalogVersion>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queuedStatusRead = read;
            wait = _lastStatusReadStart == NoTimestamp
                ? TimeSpan.Zero
                : StatusReadSpacing - _timeProvider.GetElapsedTime(_lastStatusReadStart, _timeProvider.GetTimestamp());
        }

        _ = RunStatusReadAsync(read, wait);
        return read.Task;
    }

    private async Task RunStatusReadAsync(TaskCompletionSource<CatalogVersion> read, TimeSpan wait)
    {
        try
        {
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _timeProvider).ConfigureAwait(false);
            }

            lock (_statusReadLock)
            {
                // From here on the read is in flight: later callers queue the next one.
                _queuedStatusRead = null;
                _lastStatusReadStart = _timeProvider.GetTimestamp();
            }

            using var timeout = new CancellationTokenSource(StatusStoreReadTimeout, _timeProvider);
            read.TrySetResult(await _metadataRepo.GetCatalogVersionAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            lock (_statusReadLock)
            {
                if (ReferenceEquals(_queuedStatusRead, read))
                {
                    _queuedStatusRead = null;
                }
            }

            read.TrySetException(ex);
        }
    }

    private void DisposeChangeSignal()
    {
        lock (_signalLock)
        {
            _signalDisposed = true;
            _signalTimer?.Dispose();
            _signalTimer = null;
        }

        _changeSubscription?.Dispose();
    }
}
