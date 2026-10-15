namespace Autheris.Application.Hosting;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State;
using Autheris.Domain.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>A unit of cluster-singleton periodic work. Resolved from a fresh DI scope on every cycle.</summary>
public interface IClusterScheduledJob
{
    Task ExecuteAsync(ClusterJobContext context, CancellationToken ct);
}

/// <summary>The lease a job runs under. <see cref="FencingToken"/> must be passed to every fenced downstream write.</summary>
public sealed record ClusterJobContext(long FencingToken, IDistributedLease Lease);

/// <summary>Timing of one scheduled job; re-read on every cycle so option changes apply without a restart.</summary>
public sealed record ClusterSchedule(bool Enabled, TimeSpan Interval, TimeSpan InitialDelay, TimeSpan LeaseTtl, TimeSpan MaxJitter);

internal static class ClusterScheduledJobMetrics
{
    private static readonly ConcurrentDictionary<string, long> LastSuccessByJob = new(StringComparer.Ordinal);

    private static readonly ObservableGauge<long> LastSuccessGauge = GatewayDiagnostics.Meter.CreateObservableGauge(
        "autheris_scheduled_job_last_success_timestamp_seconds",
        ObserveLastSuccess,
        unit: "s",
        description: "Unix time of the last successful scheduled job run");

    public static readonly Histogram<double> Duration = GatewayDiagnostics.Meter.CreateHistogram<double>(
        "autheris_scheduled_job_duration_seconds",
        unit: "s",
        description: "Duration of scheduled job runs that held the lease");

    public static readonly Counter<long> LeaseLost = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_scheduled_job_lease_lost_total",
        description: "Scheduled job runs aborted because the lease was lost or fenced out");

    public static readonly Counter<long> Failures = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_scheduled_job_failures_total",
        description: "Scheduled job runs that failed with an exception");

    public static readonly Counter<long> Skipped = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_scheduled_job_skipped_total",
        description: "Scheduled job cycles skipped before the job started");

    public static void RecordLastSuccess(string jobName, long unixTimeSeconds)
    {
        _ = LastSuccessGauge;
        LastSuccessByJob[jobName] = unixTimeSeconds;
    }

    private static IEnumerable<Measurement<long>> ObserveLastSuccess()
    {
        foreach (var pair in LastSuccessByJob)
        {
            yield return new Measurement<long>(pair.Value, new KeyValuePair<string, object?>("job", pair.Key));
        }
    }
}

/// <summary>
/// Base class for cluster-singleton periodic workers. Every cycle acquires a renewed, fenced
/// <see cref="IDistributedLease"/>, runs <typeparamref name="TJob"/> in a fresh scope with a token that is cancelled when
/// the lease is lost, and never runs unguarded: an unreachable cluster state store skips the cycle.
/// </summary>
public abstract class ClusterScheduledService<TJob> : BackgroundService
    where TJob : class, IClusterScheduledJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedClusterStateProvider _clusterState;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly string _jobName = typeof(TJob).Name;

    protected ClusterScheduledService(
        IServiceScopeFactory scopeFactory,
        IDistributedClusterStateProvider clusterState,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _clusterState = clusterState ?? throw new ArgumentNullException(nameof(clusterState));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The cluster-wide resource key of the lease.</summary>
    protected abstract string LeaseKey { get; }

    /// <summary>The current schedule, read from live options once per cycle.</summary>
    protected abstract ClusterSchedule GetSchedule();

    private static readonly TimeSpan MinDisabledRecheck = TimeSpan.FromMinutes(1);
    private static readonly ClusterSchedule FallbackSchedule =
        new(false, MinDisabledRecheck, TimeSpan.Zero, MinDisabledRecheck, TimeSpan.Zero);

    private ClusterSchedule _lastSchedule = FallbackSchedule;

    /// <summary>Reads the schedule; a failing options read keeps the previous schedule and never kills the host.</summary>
    private ClusterSchedule ReadSchedule()
    {
        try
        {
            _lastSchedule = GetSchedule();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Reading the schedule of scheduled job {Job} failed; the previous schedule stays in effect.", _jobName);
        }

        return _lastSchedule;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var schedule = ReadSchedule();
            if (schedule.Enabled)
            {
                var initial = schedule.InitialDelay + NextJitter(schedule.MaxJitter);
                if (initial > TimeSpan.Zero)
                {
                    await Task.Delay(initial, _time, stoppingToken).ConfigureAwait(false);
                }
            }

            var disabledLogged = false;
            while (!stoppingToken.IsCancellationRequested)
            {
                schedule = ReadSchedule();
                if (!schedule.Enabled)
                {
                    // Stay alive so that enabling the job through the options monitor takes effect without a restart.
                    if (!disabledLogged)
                    {
                        _logger.LogInformation("Scheduled job {Job} is disabled; the worker idles and re-checks its schedule.", _jobName);
                        disabledLogged = true;
                    }

                    await Task.Delay(schedule.Interval > MinDisabledRecheck ? schedule.Interval : MinDisabledRecheck, _time, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                disabledLogged = false;
                await RunCycleAsync(stoppingToken).ConfigureAwait(false);

                // Re-read per cycle so interval changes apply without a restart.
                schedule = ReadSchedule();
                await Task.Delay(schedule.Interval + NextJitter(schedule.MaxJitter), _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    private static TimeSpan NextJitter(TimeSpan max) =>
        max > TimeSpan.Zero ? TimeSpan.FromTicks((long)(Random.Shared.NextDouble() * max.Ticks)) : TimeSpan.Zero;

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        var schedule = ReadSchedule();
        IDistributedLease? lease;
        try
        {
            lease = await _clusterState.TryAcquireLeaseAsync(LeaseKey, schedule.LeaseTtl, stoppingToken).ConfigureAwait(false);
        }
        catch (ClusterStateUnavailableException ex)
        {
            // Never run unguarded and never crash the host: skip this cycle.
            _logger.LogWarning(ex, "Scheduled job {Job} skipped: cluster state unavailable.", _jobName);
            ClusterScheduledJobMetrics.Skipped.Add(1, new KeyValuePair<string, object?>("job", _jobName), new KeyValuePair<string, object?>("reason", "state_unavailable"));
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Scheduled job {Job} skipped: acquiring the lease failed unexpectedly.", _jobName);
            ClusterScheduledJobMetrics.Failures.Add(1, new KeyValuePair<string, object?>("job", _jobName));
            return;
        }

        if (lease is null)
        {
            _logger.LogDebug("Scheduled job {Job} lease is held by another cluster replica; skipping cycle.", _jobName);
            ClusterScheduledJobMetrics.Skipped.Add(1, new KeyValuePair<string, object?>("job", _jobName), new KeyValuePair<string, object?>("reason", "busy"));
            return;
        }

        await using (lease.ConfigureAwait(false))
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.LeaseLost);
                await using var scope = _scopeFactory.CreateAsyncScope();
                var job = scope.ServiceProvider.GetRequiredService<TJob>();
                await job.ExecuteAsync(new ClusterJobContext(lease.Token, lease), linked.Token).ConfigureAwait(false);

                if (lease.LeaseLost.IsCancellationRequested)
                {
                    _logger.LogWarning("Scheduled job {Job} finished after its lease was lost.", _jobName);
                    ClusterScheduledJobMetrics.LeaseLost.Add(1, new KeyValuePair<string, object?>("job", _jobName));
                }
                else
                {
                    ClusterScheduledJobMetrics.RecordLastSuccess(_jobName, _time.GetUtcNow().ToUnixTimeSeconds());
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (lease.LeaseLost.IsCancellationRequested)
            {
                _logger.LogWarning("Scheduled job {Job} was cancelled because its lease was lost.", _jobName);
                ClusterScheduledJobMetrics.LeaseLost.Add(1, new KeyValuePair<string, object?>("job", _jobName));
            }
            catch (FencingTokenRejectedException ex)
            {
                _logger.LogError(ex, "Scheduled job {Job} was fenced out by a newer lease owner.", _jobName);
                ClusterScheduledJobMetrics.LeaseLost.Add(1, new KeyValuePair<string, object?>("job", _jobName));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled job {Job} failed.", _jobName);
                ClusterScheduledJobMetrics.Failures.Add(1, new KeyValuePair<string, object?>("job", _jobName));
            }
            finally
            {
                ClusterScheduledJobMetrics.Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>("job", _jobName));
            }
        }
    }
}
