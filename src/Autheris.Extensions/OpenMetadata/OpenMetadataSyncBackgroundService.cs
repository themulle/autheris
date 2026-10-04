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

    public OpenMetadataSyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<GatewayOptions> options,
        ILogger<OpenMetadataSyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
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

        // Initial sync on startup
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var syncService = scope.ServiceProvider.GetRequiredService<IOpenMetadataSyncService>();
            await syncService.SyncPermissionsAsync(dryRun: false, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Initial OpenMetadata sync failed on startup.");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, omOptions.SyncIntervalMinutes)));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var syncService = scope.ServiceProvider.GetRequiredService<IOpenMetadataSyncService>();
                await syncService.SyncPermissionsAsync(dryRun: false, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Periodic OpenMetadata sync failed.");
            }
        }
    }
}
