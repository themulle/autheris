using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Autheris.Application.Catalog;

/// <summary>
/// Background service that performs initial snapshot loading on startup and periodic polling as a safety fallback.
/// </summary>
public sealed class CatalogSnapshotBackgroundService : BackgroundService
{
    private readonly ICatalogSnapshotProvider _snapshotProvider;
    private readonly ILogger<CatalogSnapshotBackgroundService>? _logger;
    private readonly TimeSpan _pollingInterval;
    private readonly ICatalogDirtyFlag? _dirtyFlag;
    private readonly ICatalogVersionFlusher? _flusher;

    public CatalogSnapshotBackgroundService(
        ICatalogSnapshotProvider snapshotProvider,
        ILogger<CatalogSnapshotBackgroundService>? logger = null,
        TimeSpan? pollingInterval = null,
        ICatalogDirtyFlag? dirtyFlag = null,
        ICatalogVersionFlusher? flusher = null)
    {
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _logger = logger;
        _pollingInterval = pollingInterval ?? TimeSpan.FromSeconds(30);
        _dirtyFlag = dirtyFlag;
        _flusher = flusher;
    }

    /// <summary>
    /// Repairs a failed catalog write scope flush: while the process-wide dirty flag is set, the poll forces one catalog
    /// version bump (which publishes <c>catalog:changed</c>) before the snapshot refresh, then clears the flag.
    /// </summary>
    internal async Task RepairDirtyCatalogVersionAsync(CancellationToken ct)
    {
        if (_dirtyFlag is not { IsDirty: true } || _flusher is null)
        {
            return;
        }

        try
        {
            await _flusher.FlushCatalogVersionAsync(1, 1, ct).ConfigureAwait(false);
            _dirtyFlag.Clear();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Catalog version repair after a failed write scope flush did not succeed; will retry on the next poll.");
        }
    }

    internal Task PollOnceAsync(CancellationToken ct) => PollCoreAsync(ct);

    private async Task PollCoreAsync(CancellationToken ct)
    {
        await RepairDirtyCatalogVersionAsync(ct).ConfigureAwait(false);
        await _snapshotProvider.RefreshAsync(ct).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial warm-up on startup
        try
        {
            await _snapshotProvider.RefreshAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Initial catalog snapshot warm-up failed; will retry in background loop.");
        }

        using var timer = new PeriodicTimer(_pollingInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    await PollCoreAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Periodic catalog snapshot refresh encountered an error.");
            }
        }
    }
}
