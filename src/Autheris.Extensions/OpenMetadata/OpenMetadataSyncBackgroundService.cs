using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Extensions.OpenMetadata;

public sealed class OpenMetadataSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<OpenMetadataSyncBackgroundService> _logger;
    private readonly Autheris.Application.State.IDistributedClusterStateProvider? _clusterState;

    public OpenMetadataSyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<GatewayOptions> options,
        ILogger<OpenMetadataSyncBackgroundService> logger,
        Autheris.Application.State.IDistributedClusterStateProvider? clusterState = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _clusterState = clusterState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var omOptions = _options.Value.OpenMetadata;
        if (!omOptions.Enabled)
        {
            _logger.LogInformation("OpenMetadata integration is disabled. Background sync will not run.");
            return;
        }

        _logger.LogInformation("OpenMetadata background sync service started. Sync interval: {Interval} minutes.", omOptions.SyncIntervalMinutes);

        var leaseDuration = TimeSpan.FromMinutes(Math.Max(5, omOptions.SyncIntervalMinutes));

        // Initial sync on startup
        await ExecuteSyncWithLockAsync(leaseDuration, stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, omOptions.SyncIntervalMinutes)));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ExecuteSyncWithLockAsync(leaseDuration, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteSyncWithLockAsync(TimeSpan leaseDuration, CancellationToken stoppingToken)
    {
        IAsyncDisposable? syncLock = null;
        if (_clusterState != null)
        {
            syncLock = await _clusterState.TryAcquireLockAsync("catalog:openmetadata:sync", leaseDuration, stoppingToken).ConfigureAwait(false);
            if (syncLock == null)
            {
                _logger.LogDebug("OpenMetadata sync lock held by another cluster replica; skipping sync cycle.");
                return;
            }
        }

        await using (syncLock)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var syncService = scope.ServiceProvider.GetRequiredService<IOpenMetadataSyncService>();
                await syncService.SyncPermissionsAsync(dryRun: false, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "OpenMetadata sync failed.");
            }
        }
    }
}
