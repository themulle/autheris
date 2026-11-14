namespace Autheris.Application.Catalog.Search;

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Asynchroner Hintergrund-Dienst zum Aufwärmen und kontinuierlichen Aktualisieren des Katalog-Suchindex.
/// Reagiert auf catalog:changed-Ereignisse und periodische Aktualisierungsintervalle (Issue #458).
/// </summary>
public sealed class CatalogSearchWarmupService : BackgroundService
{
    public static readonly TimeSpan MinimumRebuildInterval = TimeSpan.FromSeconds(5);

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly ICatalogSearchEngine _searchEngine;
    private readonly IOptions<CatalogSearchOptions> _options;
    private readonly ILogger<CatalogSearchWarmupService> _logger;
    private readonly IEventBus? _eventBus;
    private readonly ICatalogVersionProvider? _versionProvider;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);
    private readonly Channel<bool> _changeSignal = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly object _statusLock = new();
    private IDisposable? _eventSubscription;
    private CatalogVersion? _lastIndexedVersion;
    private DateTimeOffset? _lastRebuildStartedUtc;
    private DateTimeOffset? _lastSuccessUtc;
    private DateTimeOffset? _lastAttemptUtc;
    private string? _lastErrorCode;
    private int _pendingRebuild;
    private bool _isWaitingForCooldown;

    public bool HasPendingRebuild => Volatile.Read(ref _pendingRebuild) != 0;
    public bool IsWaitingForCooldown => Volatile.Read(ref _isWaitingForCooldown);

    public CatalogSearchIndexStatus GetStatus()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_statusLock)
        {
            return new CatalogSearchIndexStatus(
                _lastSuccessUtc,
                _lastAttemptUtc,
                _lastErrorCode,
                _lastSuccessUtc.HasValue ? now - _lastSuccessUtc.Value : null);
        }
    }

    public CatalogSearchWarmupService(
        ITableMetadataRepository metadataRepository,
        ICatalogSearchEngine searchEngine,
        IOptions<CatalogSearchOptions> options,
        ILogger<CatalogSearchWarmupService> logger,
        IEventBus? eventBus = null,
        ICatalogVersionProvider? versionProvider = null,
        TimeProvider? timeProvider = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _searchEngine = searchEngine ?? throw new ArgumentNullException(nameof(searchEngine));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _eventBus = eventBus;
        _versionProvider = versionProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled) return;

        if (_eventBus != null)
        {
            _eventSubscription = _eventBus.Subscribe<object>(
                ICatalogVersionProvider.CatalogChangedEventChannel,
                _ =>
                {
                    Interlocked.Exchange(ref _pendingRebuild, 1);
                    _changeSignal.Writer.TryWrite(true);
                    return Task.CompletedTask;
                });
        }

        if (_options.Value.WarmupOnStartup)
        {
            await RebuildSafeAsync(stoppingToken).ConfigureAwait(false);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (Volatile.Read(ref _pendingRebuild) == 0)
            {
                var refreshSeconds = _options.Value.RefreshSeconds;
                var pollInterval = refreshSeconds > 0 ? TimeSpan.FromSeconds(refreshSeconds) : Timeout.InfiniteTimeSpan;

                try
                {
                    CancellationTokenSource? delayCts = null;
                    CancellationTokenSource? linkedCts = null;
                    try
                    {
                        if (pollInterval != Timeout.InfiniteTimeSpan)
                        {
                            delayCts = new CancellationTokenSource(pollInterval, _timeProvider);
                            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, delayCts.Token);
                        }
                        else
                        {
                            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        }

                        await _changeSignal.Reader.ReadAsync(linkedCts.Token).ConfigureAwait(false);
                        await Task.Yield();
                        while (_changeSignal.Reader.TryRead(out _)) { }
                    }
                    finally
                    {
                        linkedCts?.Dispose();
                        delayCts?.Dispose();
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Timeout elapsed -> periodic check
                }
            }

            if (stoppingToken.IsCancellationRequested) break;

            await WaitForCooldownAsync(stoppingToken).ConfigureAwait(false);
            if (stoppingToken.IsCancellationRequested) break;

            // Collapse any burst accumulated before or during cooldown into a single rebuild attempt.
            while (_changeSignal.Reader.TryRead(out _)) { }
            Interlocked.Exchange(ref _pendingRebuild, 0);

            if (_versionProvider != null)
            {
                try
                {
                    var currentVersion = await _versionProvider.GetCatalogVersionAsync(stoppingToken).ConfigureAwait(false);
                    if (_lastIndexedVersion.HasValue && currentVersion == _lastIndexedVersion.Value)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Fehler beim Prüfen der Katalog-Version für den Suchindex.");
                }
            }

            await RebuildSafeAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForCooldownAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DateTimeOffset? lastStarted;
            lock (_statusLock)
            {
                lastStarted = _lastRebuildStartedUtc;
            }

            if (!lastStarted.HasValue)
            {
                return;
            }

            var elapsed = _timeProvider.GetUtcNow() - lastStarted.Value;
            var remaining = MinimumRebuildInterval - elapsed;
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
                if (_timeProvider.GetUtcNow() - lastStarted.Value >= MinimumRebuildInterval)
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

    private async Task RebuildSafeAsync(CancellationToken ct)
    {
        await _rebuildLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var attemptUtc = _timeProvider.GetUtcNow();
            lock (_statusLock)
            {
                _lastRebuildStartedUtc = attemptUtc;
            }

            CatalogVersion? targetVersion = null;
            if (_versionProvider != null)
            {
                try
                {
                    targetVersion = await _versionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    RecordOutcome(lastSuccessUtc: null, attemptUtc, CatalogSearchErrorCodes.VersionReadFailed, indexedVersion: null);
                    _logger.LogWarning(ex, "Fehler beim Lesen der Katalog-Version vor dem Index-Neuaufbau.");
                    return;
                }
            }

            _logger.LogInformation("Starte Index-Neuaufbau für den Katalog-Suchindex...");
            var tables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
            await _searchEngine.RebuildIndexAsync(tables, ct).ConfigureAwait(false);

            _lastIndexedVersion = targetVersion;
            var successUtc = _timeProvider.GetUtcNow();
            var versionTuple = targetVersion.HasValue
                ? (targetVersion.Value.StructureVersion, targetVersion.Value.ContentVersion)
                : ((long, long)?)null;
            _logger.LogInformation("Katalog-Suchindex Neuaufbau erfolgreich beendet ({Count} Tabellen).", tables.Count);

            if (_versionProvider != null && targetVersion.HasValue)
            {
                try
                {
                    var postVersion = await _versionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
                    if (postVersion != targetVersion.Value)
                    {
                        Interlocked.Exchange(ref _pendingRebuild, 1);
                        _changeSignal.Writer.TryWrite(true);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Fehler beim Nachprüfen der Katalog-Version nach dem Index-Neuaufbau.");
                }
            }

            RecordOutcome(successUtc, attemptUtc, lastErrorCode: null, versionTuple);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Katalog-Suchindex Aufbau abgebrochen (Shutdown).");
        }
        catch (Exception ex)
        {
            var errorCode = ex.Data[CatalogSearchErrorCodes.ErrorCodeDataKey] as string == CatalogSearchErrorCodes.EmbeddingFailed
                ? CatalogSearchErrorCodes.EmbeddingFailed
                : CatalogSearchErrorCodes.RebuildFailed;
            DateTimeOffset attemptUtc;
            lock (_statusLock)
            {
                attemptUtc = _lastRebuildStartedUtc ?? _timeProvider.GetUtcNow();
            }

            RecordOutcome(lastSuccessUtc: null, attemptUtc, errorCode, indexedVersion: null);
            _logger.LogError(ex, "Kritischer Fehler beim Aufbau des Katalog-Suchindex ({ErrorCode}).", errorCode);
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    private void RecordOutcome(
        DateTimeOffset? lastSuccessUtc,
        DateTimeOffset lastAttemptUtc,
        string? lastErrorCode,
        (long StructureVersion, long ContentVersion)? indexedVersion)
    {
        lock (_statusLock)
        {
            if (lastSuccessUtc.HasValue)
            {
                _lastSuccessUtc = lastSuccessUtc;
            }

            _lastAttemptUtc = lastAttemptUtc;
            _lastErrorCode = lastErrorCode;
        }

        if (_searchEngine is ICatalogSearchStatusRecorder recorder)
        {
            recorder.RecordRebuildOutcome(lastSuccessUtc, lastAttemptUtc, lastErrorCode, indexedVersion);
        }
    }

    public override void Dispose()
    {
        _eventSubscription?.Dispose();
        _rebuildLock.Dispose();
        base.Dispose();
    }
}
