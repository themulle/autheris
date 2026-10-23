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
/// Issue #286: the worker never runs DDL; a missing table or a failing store is reported (not retried with DDL) and
/// the repeated failure is logged without flooding the log (SEC-286-5/6).
/// </summary>
public sealed class ConsumerUsageFlushWorker : BackgroundService
{
    /// <summary>Maximum time between two error-level reports of a persisting failure.</summary>
    internal static readonly TimeSpan ErrorReEmitInterval = TimeSpan.FromMinutes(15);

    /// <summary>Maximum number of failures between two error-level reports of a persisting failure.</summary>
    internal const int ErrorReEmitEveryFailures = 100;

    private readonly ConsumerUsageRecorder _recorder;
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptions<AuditOptions> _options;
    private readonly ILogger<ConsumerUsageFlushWorker> _logger;
    private readonly TimeSpan _flushInterval;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _lastPurgeAt = DateTimeOffset.MinValue;
    private long _consecutiveFailures;
    private DateTimeOffset _firstFailureAt;
    private DateTimeOffset _lastErrorLoggedAt;

    public ConsumerUsageFlushWorker(
        ConsumerUsageRecorder recorder,
        IServiceProvider serviceProvider,
        IOptions<AuditOptions> options,
        ILogger<ConsumerUsageFlushWorker> logger,
        TimeSpan? flushInterval = null,
        TimeProvider? timeProvider = null)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(15);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Number of consecutive failed flush cycles (health signal); zero while the store accepts writes.</summary>
    public long ConsecutiveFailureCount => Volatile.Read(ref _consecutiveFailures);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_flushInterval);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await FlushOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>One flush and purge cycle. Failures are logged and swallowed; only cancellation propagates.</summary>
    internal async Task FlushOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetService<IConsumerAggregationRepository>() ??
                       scope.ServiceProvider.GetService<IGovernanceRepository>();

            if (repo != null)
            {
                await _recorder.FlushAsync(repo, ct).ConfigureAwait(false);

                // Daily retention purge check
                var now = _timeProvider.GetUtcNow();
                if (now - _lastPurgeAt > TimeSpan.FromHours(24))
                {
                    int retentionDays = _options.Value?.Retention?.DataAccessDays ?? 400;
                    await repo.PurgeExpiredConsumerMetricsAsync(retentionDays, now, ct).ConfigureAwait(false);
                    _lastPurgeAt = now;
                }
            }

            RecordSuccess();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
        }
    }

    private void RecordSuccess()
    {
        long failures = Interlocked.Exchange(ref _consecutiveFailures, 0);
        if (failures > 0)
        {
            _logger.LogInformation("Consumer usage flush recovered after {FailureCount} failed cycle(s).", failures);
        }
    }

    // SEC-286-5: only the exception type and the provider message are logged, never the exception object (no parameter dumps),
    // and never tenant, application, client or actor values.
    private void RecordFailure(Exception ex)
    {
        var now = _timeProvider.GetUtcNow();
        long failures = Interlocked.Increment(ref _consecutiveFailures);
        if (failures == 1)
        {
            _firstFailureAt = now;
        }

        bool reEmit = failures == 1
                      || now - _lastErrorLoggedAt >= ErrorReEmitInterval
                      || failures % ErrorReEmitEveryFailures == 0;

        if (reEmit)
        {
            _lastErrorLoggedAt = now;
            _logger.LogError(
                new EventId(1, "ConsumerUsageFlushFailed"),
                "Failed to flush consumer usage metrics to the governance database ({ExceptionType}: {ProviderMessage}). Consecutive failures: {FailureCount}, first failure at {FirstFailureAt:O}. The CONSUMER_APP_* tables must exist (baseline migration); the runtime never creates them.",
                ex.GetType().Name,
                ex.Message,
                failures,
                _firstFailureAt);
        }
        else
        {
            _logger.LogDebug(
                "Consumer usage flush still failing ({ExceptionType}); consecutive failures: {FailureCount}.",
                ex.GetType().Name,
                failures);
        }
    }
}
