namespace Autheris.Infrastructure.Observability;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Architecture Phase 2 / Issue #114: Periodic background hosted service that flushes in-memory consumer metrics
/// to persistent storage (15s interval) and executes daily retention purges decoupled from the audit log trail.
/// </summary>
public sealed class ConsumerUsageFlushWorker : BackgroundService
{
    private readonly ConsumerUsageRecorder _recorder;
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptions<AuditOptions> _options;
    private readonly ILogger<ConsumerUsageFlushWorker> _logger;
    private readonly TimeSpan _flushInterval;
    private DateTimeOffset _lastPurgeAt = DateTimeOffset.MinValue;

    public ConsumerUsageFlushWorker(
        ConsumerUsageRecorder recorder,
        IServiceProvider serviceProvider,
        IOptions<AuditOptions> options,
        ILogger<ConsumerUsageFlushWorker> logger,
        TimeSpan? flushInterval = null)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(15);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_flushInterval);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var repo = scope.ServiceProvider.GetService<IConsumerAggregationRepository>() ??
                           scope.ServiceProvider.GetService<IGovernanceRepository>();

                if (repo != null)
                {
                    await _recorder.FlushAsync(repo, stoppingToken).ConfigureAwait(false);

                    // Daily retention purge check
                    var now = DateTimeOffset.UtcNow;
                    if (now - _lastPurgeAt > TimeSpan.FromHours(24))
                    {
                        int retentionDays = _options.Value?.Retention?.DataAccessDays ?? 400;
                        await repo.PurgeExpiredConsumerMetricsAsync(retentionDays, now, stoppingToken).ConfigureAwait(false);
                        _lastPurgeAt = now;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to flush consumer usage metrics to persistent repository.");
            }
        }
    }
}
