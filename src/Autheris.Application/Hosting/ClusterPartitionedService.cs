namespace Autheris.Application.Hosting;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>One independently leased unit of cluster work (for example one tracked table). The key is the lease resource key.</summary>
public sealed record ClusterPartition(string Key, ClusterSchedule Schedule);

/// <summary>
/// Generalisation of <see cref="ClusterScheduledService{TJob}"/> to many partitions: every partition has its own renewed, fenced
/// <see cref="IDistributedLease"/> (resource key = <see cref="ClusterPartition.Key"/>) and its own loop, so replicas can share the
/// partitions between them and one slow or failing partition never delays the others. The partition set and the schedules are
/// re-read from live options on every reconcile pass, so configuration changes start, stop and re-time loops without a restart.
/// A partition never runs unguarded: an unreachable cluster state store skips the cycle, and a lost lease cancels the running work.
/// The job metrics of <see cref="ClusterScheduledService{TJob}"/> are reused with a partition-free <c>job</c> label (cardinality).
/// </summary>
public abstract class ClusterPartitionedService : BackgroundService
{
    private static readonly TimeSpan MinDisabledRecheck = TimeSpan.FromMinutes(1);

    private readonly IDistributedClusterStateProvider _clusterState;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly string _jobName;
    private readonly object _gate = new();
    private readonly Dictionary<string, PartitionLoop> _loops = new(StringComparer.Ordinal);
    private CancellationToken _stopping;

    protected ClusterPartitionedService(IDistributedClusterStateProvider clusterState, ILogger logger, TimeProvider? timeProvider = null)
    {
        _clusterState = clusterState ?? throw new ArgumentNullException(nameof(clusterState));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;
        _jobName = GetType().Name;
    }

    /// <summary>The partitions that should currently run, read from live options once per reconcile pass.</summary>
    protected abstract IReadOnlyList<ClusterPartition> GetPartitions();

    /// <summary>One run of a partition under its lease. <paramref name="ct"/> is cancelled when the lease is lost.</summary>
    protected abstract Task ExecutePartitionAsync(ClusterPartition partition, ClusterJobContext context, CancellationToken ct);

    /// <summary>How often the partition set is re-read.</summary>
    protected virtual TimeSpan ReconcileInterval => TimeSpan.FromSeconds(5);

    /// <summary>Keys of the partitions that currently have a running loop.</summary>
    public IReadOnlyCollection<string> ActivePartitionKeys
    {
        get
        {
            lock (_gate)
            {
                return _loops.Keys.ToArray();
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ReconcileAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(ReconcileInterval, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
        finally
        {
            await StopAllAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Re-reads the partitions: starts a loop for every new key, stops the loop of every removed key and hands changed schedules to
    /// the running loops. A failing read keeps the previous partition set (never kills the host).
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ClusterPartition> wanted;
        try
        {
            wanted = GetPartitions();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Reading the partitions of scheduled job {Job} failed; the previous partition set stays in effect.", _jobName);
            return;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        List<PartitionLoop> removed = [];
        lock (_gate)
        {
            foreach (var partition in wanted)
            {
                if (!keys.Add(partition.Key))
                {
                    continue; // a duplicate key is one partition
                }

                if (_loops.TryGetValue(partition.Key, out var existing))
                {
                    existing.Partition = partition;
                }
                else
                {
                    var loop = new PartitionLoop(partition);
                    _loops[partition.Key] = loop;
                    var token = CancellationTokenSource.CreateLinkedTokenSource(_stopping, loop.Cts.Token);
                    loop.Linked = token;
                    loop.Task = Task.Run(() => RunLoopAsync(loop, token.Token), CancellationToken.None);
                }
            }

            foreach (var key in _loops.Keys.Where(k => !keys.Contains(k)).ToArray())
            {
                removed.Add(_loops[key]);
                _loops.Remove(key);
            }
        }

        foreach (var loop in removed)
        {
            await StopLoopAsync(loop).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
    }

    private async Task StopAllAsync()
    {
        List<PartitionLoop> all;
        lock (_gate)
        {
            all = _loops.Values.ToList();
            _loops.Clear();
        }

        foreach (var loop in all)
        {
            await StopLoopAsync(loop).ConfigureAwait(false);
        }
    }

    private static async Task StopLoopAsync(PartitionLoop loop)
    {
        await loop.Cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (loop.Task is { } task)
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        finally
        {
            loop.Linked?.Dispose();
            loop.Cts.Dispose();
        }
    }

    private async Task RunLoopAsync(PartitionLoop loop, CancellationToken ct)
    {
        try
        {
            var first = true;
            while (!ct.IsCancellationRequested)
            {
                var partition = loop.Partition;
                var schedule = partition.Schedule;
                if (!schedule.Enabled)
                {
                    await Task.Delay(schedule.Interval > MinDisabledRecheck ? schedule.Interval : MinDisabledRecheck, _time, ct).ConfigureAwait(false);
                    continue;
                }

                if (first)
                {
                    first = false;
                    var initial = schedule.InitialDelay + NextJitter(schedule.MaxJitter);
                    if (initial > TimeSpan.Zero)
                    {
                        await Task.Delay(initial, _time, ct).ConfigureAwait(false);
                    }
                }

                await RunPartitionCycleAsync(loop.Partition, ct).ConfigureAwait(false);

                schedule = loop.Partition.Schedule;
                await Task.Delay(schedule.Interval + NextJitter(schedule.MaxJitter), _time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // partition removed or host shutdown
        }
    }

    private static TimeSpan NextJitter(TimeSpan max) =>
        max > TimeSpan.Zero ? TimeSpan.FromTicks((long)(Random.Shared.NextDouble() * max.Ticks)) : TimeSpan.Zero;

    /// <summary>
    /// One guarded cycle of one partition: acquire the fenced lease, run <see cref="ExecutePartitionAsync"/> with a token cancelled on
    /// lease loss, release the lease. Returns true when the partition work ran to completion under the lease. Public as a deterministic
    /// seam: tests drive cycles without waiting for the timers.
    /// </summary>
    public async Task<bool> RunPartitionCycleAsync(ClusterPartition partition, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(partition);
        IDistributedLease? lease;
        try
        {
            lease = await _clusterState.TryAcquireLeaseAsync(partition.Key, partition.Schedule.LeaseTtl, stoppingToken).ConfigureAwait(false);
        }
        catch (ClusterStateUnavailableException ex)
        {
            // Never run unguarded and never crash the host: skip this cycle.
            _logger.LogWarning(ex, "Scheduled job {Job} skipped a partition: cluster state unavailable.", _jobName);
            ClusterScheduledJobMetrics.Skipped.Add(1, new KeyValuePair<string, object?>("job", _jobName), new KeyValuePair<string, object?>("reason", "state_unavailable"));
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Scheduled job {Job} skipped a partition: acquiring the lease failed unexpectedly.", _jobName);
            ClusterScheduledJobMetrics.Failures.Add(1, new KeyValuePair<string, object?>("job", _jobName));
            return false;
        }

        if (lease is null)
        {
            ClusterScheduledJobMetrics.Skipped.Add(1, new KeyValuePair<string, object?>("job", _jobName), new KeyValuePair<string, object?>("reason", "busy"));
            return false;
        }

        await using (lease.ConfigureAwait(false))
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.LeaseLost);
                await ExecutePartitionAsync(partition, new ClusterJobContext(lease.Token, lease), linked.Token).ConfigureAwait(false);

                if (lease.LeaseLost.IsCancellationRequested)
                {
                    _logger.LogWarning("Scheduled job {Job} finished a partition after its lease was lost.", _jobName);
                    ClusterScheduledJobMetrics.LeaseLost.Add(1, new KeyValuePair<string, object?>("job", _jobName));
                    return false;
                }

                ClusterScheduledJobMetrics.RecordLastSuccess(_jobName, _time.GetUtcNow().ToUnixTimeSeconds());
                return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (lease.LeaseLost.IsCancellationRequested)
            {
                _logger.LogWarning("Scheduled job {Job} cancelled a partition because its lease was lost.", _jobName);
                ClusterScheduledJobMetrics.LeaseLost.Add(1, new KeyValuePair<string, object?>("job", _jobName));
                return false;
            }
            catch (FencingTokenRejectedException ex)
            {
                _logger.LogError(ex, "Scheduled job {Job} partition was fenced out by a newer lease owner.", _jobName);
                ClusterScheduledJobMetrics.LeaseLost.Add(1, new KeyValuePair<string, object?>("job", _jobName));
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled job {Job} partition failed.", _jobName);
                ClusterScheduledJobMetrics.Failures.Add(1, new KeyValuePair<string, object?>("job", _jobName));
                return false;
            }
            finally
            {
                ClusterScheduledJobMetrics.Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>("job", _jobName));
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await StopAllAsync().ConfigureAwait(false);
    }

    private sealed class PartitionLoop(ClusterPartition partition)
    {
        private ClusterPartition _partition = partition;

        public ClusterPartition Partition
        {
            get => Volatile.Read(ref _partition);
            set => Volatile.Write(ref _partition, value);
        }

        public CancellationTokenSource Cts { get; } = new();

        public CancellationTokenSource? Linked { get; set; }

        public Task? Task { get; set; }
    }
}
