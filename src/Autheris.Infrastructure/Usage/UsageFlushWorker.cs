namespace Autheris.Infrastructure.Usage;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Usage;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>Periodically swaps and flushes aggregated usage counters to persistent storage.</summary>
public sealed class UsageFlushWorker : BackgroundService
{
    internal static readonly TimeSpan ErrorReEmitInterval = TimeSpan.FromMinutes(15);
    internal const int ErrorReEmitEveryFailures = 100;
    private static readonly TimeSpan StopFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly IUsageAggregator _aggregator;
    private readonly IUsageRepository _repository;
    private readonly IOptions<UsageAccountingOptions> _options;
    private readonly ILogger<UsageFlushWorker> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IUsageQuotaService? _quotaService;

    private int _consecutiveFailures;
    private DateTimeOffset _lastErrorLoggedAt = DateTimeOffset.MinValue;
    private DateOnly _lastPurgeDate = DateOnly.MinValue;

    public UsageFlushWorker(
        IUsageAggregator aggregator,
        IUsageRepository repository,
        IOptions<UsageAccountingOptions> options,
        ILogger<UsageFlushWorker> logger,
        TimeProvider? timeProvider = null,
        IUsageQuotaService? quotaService = null)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _quotaService = quotaService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshQuotasAsync(stoppingToken).ConfigureAwait(false);
        int seconds = Math.Max(5, _options.Value.FlushIntervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await FlushOnceAsync(stoppingToken).ConfigureAwait(false);
            await PurgeIfDueAsync(stoppingToken).ConfigureAwait(false);
            await RefreshQuotasAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(StopFlushTimeout);
        await FlushOnceAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    private async Task RefreshQuotasAsync(CancellationToken ct)
    {
        if (_quotaService is null)
        {
            return;
        }

        try
        {
            await _quotaService.RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Usage quota refresh cycle failed.");
        }
    }

    internal async Task FlushOnceAsync(CancellationToken ct)
    {
        var snapshot = _aggregator.Swap();
        if (IsEmpty(snapshot))
        {
            _consecutiveFailures = 0;
            return;
        }

        try
        {
            await _repository.AccumulateAsync(snapshot, ct).ConfigureAwait(false);
            _consecutiveFailures = 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _aggregator.Merge(snapshot);
        }
        catch (Exception ex)
        {
            int failures = ++_consecutiveFailures;
            int maxRetries = Math.Max(1, _options.Value.MaxFlushRetries);
            if (failures < maxRetries)
            {
                _aggregator.Merge(snapshot);
            }
            else
            {
                long dropped = CountEvents(snapshot);
                if (dropped > 0)
                {
                    UsageDiagnostics.DroppedCounter.Add(dropped);
                }

                _consecutiveFailures = 0;
            }

            LogFlushFailure(ex, failures);
        }
    }

    internal async Task PurgeIfDueAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        if (today <= _lastPurgeDate)
        {
            return;
        }

        try
        {
            int retention = Math.Max(1, _options.Value.RetentionDays);
            await _repository.PurgeAsync(today.AddDays(-retention), ct: ct).ConfigureAwait(false);
            _lastPurgeDate = today;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Usage retention purge failed ({ExceptionType}).", ex.GetType().Name);
        }
    }

    private void LogFlushFailure(Exception ex, int failures)
    {
        var now = _timeProvider.GetUtcNow();
        bool emit = failures == 1
                    || now - _lastErrorLoggedAt >= ErrorReEmitInterval
                    || failures % ErrorReEmitEveryFailures == 0;
        if (emit)
        {
            _lastErrorLoggedAt = now;
            _logger.LogError(
                "Usage flush failed ({ExceptionType}, consecutive failures: {Failures}).",
                ex.GetType().Name,
                failures);
        }
    }

    private static bool IsEmpty(UsageGenerationSnapshot snapshot) =>
        snapshot.Series.Count == 0
        && snapshot.GrantSeries.Count == 0
        && snapshot.GroupSets.Count == 0
        && snapshot.GrantRefs.Count == 0;

    private static long CountEvents(UsageGenerationSnapshot snapshot)
    {
        long total = 0;
        foreach (var entry in snapshot.Series.Values)
        {
            total += Math.Max(entry.Requests, entry.Queries);
        }

        return Math.Max(total, snapshot.GrantSeries.Count);
    }
}
