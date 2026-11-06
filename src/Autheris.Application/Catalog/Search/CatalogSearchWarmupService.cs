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
    private IDisposable? _eventSubscription;
    private CatalogVersion? _lastIndexedVersion;

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
            var refreshSeconds = _options.Value.RefreshSeconds;
            var pollInterval = refreshSeconds > 0 ? TimeSpan.FromSeconds(refreshSeconds) : Timeout.InfiniteTimeSpan;

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                if (pollInterval != Timeout.InfiniteTimeSpan)
                {
                    timeoutCts.CancelAfter(pollInterval);
                }

                await _changeSignal.Reader.ReadAsync(timeoutCts.Token).ConfigureAwait(false);

                // Drain any additional signals that arrived in quick succession
                while (_changeSignal.Reader.TryRead(out _)) { }

                // Small debounce delay to coalesce rapid consecutive changes
                await Task.Delay(50, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Timeout elapsed -> periodic check
            }

            if (stoppingToken.IsCancellationRequested) break;

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

    private async Task RebuildSafeAsync(CancellationToken ct)
    {
        await _rebuildLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("Starte Index-Neuaufbau für den Katalog-Suchindex...");
            var tables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
            await _searchEngine.RebuildIndexAsync(tables, ct).ConfigureAwait(false);
            if (_versionProvider != null)
            {
                _lastIndexedVersion = await _versionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
            }
            _logger.LogInformation("Katalog-Suchindex Neuaufbau erfolgreich beendet ({Count} Tabellen).", tables.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Katalog-Suchindex Aufbau abgebrochen (Shutdown).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kritischer Fehler beim Aufbau des Katalog-Suchindex.");
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    public override void Dispose()
    {
        _eventSubscription?.Dispose();
        _rebuildLock.Dispose();
        base.Dispose();
    }
}
