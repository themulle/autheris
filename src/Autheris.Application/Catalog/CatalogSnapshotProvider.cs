using System.Threading.Channels;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

namespace Autheris.Application.Catalog;

/// <summary>
/// Maintains an immutable in-memory <see cref="CatalogSnapshot"/> updated monotonically.
/// Change events are coalesced through a bounded capacity-1 signal channel with a minimum refresh interval
/// and a 10-minute full-reload backstop.
/// </summary>
public sealed class CatalogSnapshotProvider : ICatalogSnapshotProvider, IDisposable, IAsyncDisposable
{
    public static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan FullReloadBackstopInterval = TimeSpan.FromMinutes(10);
    public const int DegradedFailureThreshold = 3;
    public const int MaxDeltaVersionSpan = 500;

    private readonly IGovernanceRepository _governanceRepo;
    private readonly ICatalogVersionProvider _versionProvider;
    private readonly IEventBus? _eventBus;
    private readonly ILogger<CatalogSnapshotProvider>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ICatalogDirtyFlag? _dirtyFlag;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Channel<bool> _changeSignal = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _timingLock = new();
    private readonly Task? _drainTask;

    private CatalogSnapshot _currentSnapshot;
    private IDisposable? _eventSubscription;
    private DateTimeOffset? _lastRefreshCompletedUtc;
    private DateTimeOffset _lastFullReloadUtc = DateTimeOffset.MinValue;
    private long _lastLoadedVersion;
    private bool _initialized;
    private int _consecutiveFailures;
    private int _pendingSignal;
    private bool _isWaitingForCooldown;
    private int _disposed;

    public CatalogSnapshot CurrentSnapshot => Volatile.Read(ref _currentSnapshot);

    public bool IsInitialized => Volatile.Read(ref _initialized);

    public bool IsDegraded => Volatile.Read(ref _consecutiveFailures) >= DegradedFailureThreshold;

    internal bool IsWaitingForCooldown => Volatile.Read(ref _isWaitingForCooldown);

    internal bool HasPendingSignal => Volatile.Read(ref _pendingSignal) != 0;

    public CatalogSnapshotProvider(
        IGovernanceRepository governanceRepo,
        ICatalogVersionProvider versionProvider,
        IEventBus? eventBus = null,
        ILogger<CatalogSnapshotProvider>? logger = null,
        TimeProvider? timeProvider = null,
        ICatalogDirtyFlag? dirtyFlag = null)
    {
        _governanceRepo = governanceRepo ?? throw new ArgumentNullException(nameof(governanceRepo));
        _versionProvider = versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));
        _eventBus = eventBus;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _dirtyFlag = dirtyFlag;
        _currentSnapshot = CatalogSnapshot.Empty(new CatalogVersion(1, 1));

        if (_eventBus != null)
        {
            var token = _disposeCts.Token;
            _eventSubscription = _eventBus.Subscribe<object>(
                ICatalogVersionProvider.CatalogChangedEventChannel,
                _ =>
                {
                    SignalChange();
                    return Task.CompletedTask;
                });
            _drainTask = Task.Run(() => RunDrainLoopAsync(token), CancellationToken.None);
        }
    }

    public async ValueTask<CatalogSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        if (!Volatile.Read(ref _initialized))
        {
            await RefreshAsync(ct).ConfigureAwait(false);
        }

        if (!Volatile.Read(ref _initialized))
        {
            throw new Autheris.Domain.Exceptions.CatalogSnapshotNotReadyException();
        }

        return Volatile.Read(ref _currentSnapshot);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CatalogVersion dbVersion;
            try
            {
                dbVersion = await _versionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _consecutiveFailures);
                _logger?.LogError(ex, "Failed to read catalog version during catalog snapshot refresh; retaining existing snapshot.");
                return;
            }

            var current = Volatile.Read(ref _currentSnapshot);
            var now = _timeProvider.GetUtcNow();
            var backstopDue = _lastFullReloadUtc == DateTimeOffset.MinValue ||
                              (now - _lastFullReloadUtc) >= FullReloadBackstopInterval;

            // Monotonicity guard: ignore stale or regressed versions.
            if (_initialized)
            {
                if (dbVersion.StructureVersion < current.Version.StructureVersion ||
                    dbVersion.ContentVersion < current.Version.ContentVersion)
                {
                    _logger?.LogWarning(
                        "Detected stale catalog version {NewStructure}.{NewContent} when current is {CurStructure}.{CurContent}; rejecting snapshot refresh to protect monotonicity.",
                        dbVersion.StructureVersion, dbVersion.ContentVersion,
                        current.Version.StructureVersion, current.Version.ContentVersion);
                    return;
                }

                if (dbVersion.StructureVersion == current.Version.StructureVersion &&
                    dbVersion.ContentVersion == current.Version.ContentVersion &&
                    !backstopDue)
                {
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    return;
                }
            }

            var dbCombinedVersion = dbVersion.StructureVersion + dbVersion.ContentVersion;
            var canAttemptDelta =
                _initialized &&
                !backstopDue &&
                _lastLoadedVersion > 0 &&
                (_dirtyFlag is null || !_dirtyFlag.IsDirty) &&
                current.TableCount > 0 &&
                dbCombinedVersion > _lastLoadedVersion &&
                (dbCombinedVersion - _lastLoadedVersion) <= MaxDeltaVersionSpan;

            if (canAttemptDelta)
            {
                var deltaApplied = await TryApplyDeltaRefreshAsync(current, dbVersion, dbCombinedVersion, ct).ConfigureAwait(false);
                if (deltaApplied)
                {
                    return;
                }
            }

            IReadOnlyList<TableMetadata> tables;
            try
            {
                tables = await CatalogTablePageReader.ReadAllAsync(
                    _governanceRepo, new CatalogTableQuery(IncludeInactive: false), ct: ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _consecutiveFailures);
                _logger?.LogError(ex, "Failed to load tables during catalog snapshot refresh; retaining existing snapshot.");
                return;
            }

            IReadOnlyList<TableRelation> relations;
            try
            {
                relations = await _governanceRepo.GetAllRelationsAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _consecutiveFailures);
                _logger?.LogError(ex, "Failed to load relations during catalog snapshot refresh; retaining existing snapshot.");
                return;
            }

            var capturedAt = _timeProvider.GetUtcNow();
            var newSnapshot = CatalogSnapshot.Create(
                dbVersion,
                tables,
                relations,
                degradedEntityCount: 0,
                capturedAt: capturedAt);

            Volatile.Write(ref _currentSnapshot, newSnapshot);
            _lastFullReloadUtc = capturedAt;
            _lastLoadedVersion = dbCombinedVersion;
            _initialized = true;
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            _logger?.LogInformation(
                "Catalog snapshot updated to version {StructureVersion}.{ContentVersion} ({TableCount} tables, {RelationCount} relations).",
                dbVersion.StructureVersion, dbVersion.ContentVersion, tables.Count, relations.Count);
        }
        finally
        {
            lock (_timingLock)
            {
                _lastRefreshCompletedUtc = _timeProvider.GetUtcNow();
            }

            _refreshLock.Release();
        }
    }

    private async Task<bool> TryApplyDeltaRefreshAsync(
        CatalogSnapshot current,
        CatalogVersion dbVersion,
        long dbCombinedVersion,
        CancellationToken ct)
    {
        try
        {
            var changed = await _governanceRepo.GetTablesChangedSinceAsync(_lastLoadedVersion, ct).ConfigureAwait(false);
            if (changed.Count == 0 && dbVersion.StructureVersion > current.Version.StructureVersion)
            {
                _logger?.LogInformation(
                    "Catalog structure version advanced from {OldStructure} to {NewStructure} with zero changed table entries; falling back to full reload.",
                    current.Version.StructureVersion, dbVersion.StructureVersion);
                return false;
            }

            if (changed.Count > 1 && changed.Count * 5 >= current.TableCount)
            {
                _logger?.LogInformation(
                    "Catalog delta contains {ChangedCount} of {TableCount} tables (>= 20%); falling back to full reload.",
                    changed.Count, current.TableCount);
                return false;
            }

            IReadOnlyList<TableMetadata> updatedTables = changed.Count > 0
                ? await _governanceRepo.GetTablesAsync(changed, ct).ConfigureAwait(false)
                : Array.Empty<TableMetadata>();

            IReadOnlyList<TableRelation>? relations = dbVersion.StructureVersion > current.Version.StructureVersion
                ? await _governanceRepo.GetAllRelationsAsync(ct).ConfigureAwait(false)
                : null;

            var capturedAt = _timeProvider.GetUtcNow();
            var updatedSnapshot = current.WithDelta(
                dbVersion,
                changed,
                updatedTables,
                relations,
                degradedEntityCount: 0,
                capturedAt: capturedAt);

            Volatile.Write(ref _currentSnapshot, updatedSnapshot);
            _lastLoadedVersion = dbCombinedVersion;
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            _logger?.LogInformation(
                "Catalog snapshot delta-updated to version {StructureVersion}.{ContentVersion} ({ChangedCount} changed, {TableCount} tables).",
                dbVersion.StructureVersion, dbVersion.ContentVersion, changed.Count, updatedSnapshot.TableCount);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Delta catalog refresh failed at version {StructureVersion}.{ContentVersion}; falling back to full reload.", dbVersion.StructureVersion, dbVersion.ContentVersion);
            return false;
        }
    }

    private void SignalChange()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            Interlocked.Exchange(ref _pendingSignal, 1);
            _changeSignal.Writer.TryWrite(true);
        }
    }

    private async Task RunDrainLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (Volatile.Read(ref _pendingSignal) == 0)
                {
                    await _changeSignal.Reader.ReadAsync(ct).ConfigureAwait(false);
                    await Task.Yield();
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                await WaitForCooldownAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                // Drain pre-refresh signals before reading the latest version inside RefreshAsync so any
                // event arriving during the refresh leaves a pending signal for a follow-up check.
                while (_changeSignal.Reader.TryRead(out _)) { }
                Interlocked.Exchange(ref _pendingSignal, 0);

                try
                {
                    await RefreshAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Background refresh of catalog snapshot triggered by event failed.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
        }
    }

    private async Task WaitForCooldownAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DateTimeOffset? lastCompleted;
            lock (_timingLock)
            {
                lastCompleted = _lastRefreshCompletedUtc;
            }

            if (!lastCompleted.HasValue)
            {
                return;
            }

            var elapsed = _timeProvider.GetUtcNow() - lastCompleted.Value;
            var remaining = MinimumRefreshInterval - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(static (state, token) => ((TaskCompletionSource)state!).TrySetCanceled(token), tcs);
            using var timer = _timeProvider.CreateTimer(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                tcs,
                remaining,
                Timeout.InfiniteTimeSpan);
            Volatile.Write(ref _isWaitingForCooldown, true);
            try
            {
                if (_timeProvider.GetUtcNow() - lastCompleted.Value >= MinimumRefreshInterval)
                {
                    return;
                }

                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _isWaitingForCooldown, false);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _eventSubscription?.Dispose();
            _disposeCts.Cancel();
            _changeSignal.Writer.TryComplete();
            if (_drainTask is null or { IsCompleted: true })
            {
                _disposeCts.Dispose();
                _refreshLock.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_drainTask != null)
        {
            try
            {
                await _drainTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Catalog snapshot drain loop ended during disposal.");
            }
        }

        _disposeCts.Dispose();
        _refreshLock.Dispose();
    }
}
