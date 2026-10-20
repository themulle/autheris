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

    public CatalogSnapshotBackgroundService(
        ICatalogSnapshotProvider snapshotProvider,
        ILogger<CatalogSnapshotBackgroundService>? logger = null,
        TimeSpan? pollingInterval = null)
    {
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _logger = logger;
        _pollingInterval = pollingInterval ?? TimeSpan.FromSeconds(30);
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
                    await _snapshotProvider.RefreshAsync(stoppingToken).ConfigureAwait(false);
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
