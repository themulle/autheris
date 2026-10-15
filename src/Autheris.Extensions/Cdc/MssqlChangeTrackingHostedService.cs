namespace Autheris.Extensions.Cdc;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Hosting;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Autheris.Application.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Background worker service polling MSSQL Change Tracking tables on a configurable interval.
/// Zero-Kafka Realtime CDC Engine (F-CDC-02). Runs on exactly one cluster replica at a time under a renewed,
/// fenced lease (<see cref="ClusterScheduledService{TJob}"/>); the fencing token guards the watermark commits.
/// </summary>
public sealed class MssqlChangeTrackingHostedService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<GatewayOptions> options,
    IDistributedClusterStateProvider clusterState,
    IMssqlWatermarkStore watermarkStore,
    IHostEnvironment environment,
    ILogger<MssqlChangeTrackingHostedService> logger,
    TimeProvider? timeProvider = null)
    : ClusterScheduledService<MssqlChangeTrackingPollJob>(scopeFactory, clusterState, logger, timeProvider)
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // A watermark lost on restart would replay or skip changes; only Development may run without a durable store.
        if (options.CurrentValue.MssqlChangeTracking.Enabled && !watermarkStore.IsDurable && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "MSSQL Change Tracking requires a durable watermark store outside Development (the governance database store is registered by the host).");
        }

        return base.StartAsync(cancellationToken);
    }

    private object? _parsedSource;
    private int _parsedCount;

    protected override string LeaseKey => "cdc:mssql:poll";

    protected override ClusterSchedule GetSchedule()
    {
        var cdc = options.CurrentValue.MssqlChangeTracking;
        var interval = TimeSpan.FromMilliseconds(Math.Max(250, cdc.PollingIntervalMilliseconds));
        if (!ReferenceEquals(cdc.TrackedTables, _parsedSource))
        {
            _parsedCount = ParseTrackedTables(cdc.TrackedTables).Count;
            _parsedSource = cdc.TrackedTables;
        }

        var enabled = cdc.Enabled && _parsedCount > 0;
        return new ClusterSchedule(
            enabled,
            interval,
            InitialDelay: TimeSpan.Zero,
            LeaseTtl: TimeSpan.FromSeconds(Math.Max(15, 3 * interval.TotalSeconds)),
            MaxJitter: TimeSpan.Zero);
    }

    public static List<TableIdentifier> ParseTrackedTables(IEnumerable<string>? tableStrings)
    {
        var list = new List<TableIdentifier>();
        if (tableStrings == null) return list;

        foreach (var str in tableStrings)
        {
            if (string.IsNullOrWhiteSpace(str)) continue;
            var parts = str.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 3)
            {
                list.Add(new TableIdentifier(parts[0], parts[1], parts[2]));
            }
            else if (parts.Length == 2)
            {
                list.Add(new TableIdentifier(parts[0], "dbo", parts[1]));
            }
            else if (parts.Length == 1)
            {
                list.Add(new TableIdentifier("default", "dbo", parts[0]));
            }
        }

        return list;
    }
}

/// <summary>
/// Remembers tables whose polling failed with a persistent error (expired watermark, invalid configuration) so the job backs
/// off exponentially and logs the failure once instead of every polling cycle.
/// </summary>
public sealed class CdcPollFailureTracker(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TableIdentifier, (int Failures, DateTimeOffset NextAttempt)> _state = new();

    public bool ShouldSkip(TableIdentifier table) =>
        _state.TryGetValue(table, out var entry) && _time.GetUtcNow() < entry.NextAttempt;

    /// <summary>Records a persistent failure; returns true for the first failure of a streak (the one to log).</summary>
    public bool RecordFailure(TableIdentifier table)
    {
        var first = false;
        _state.AddOrUpdate(
            table,
            _ =>
            {
                first = true;
                return (1, _time.GetUtcNow() + TimeSpan.FromSeconds(5));
            },
            (_, existing) =>
            {
                var failures = existing.Failures + 1;
                var delay = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 5 * Math.Pow(2, Math.Min(failures, 10))));
                return (failures, _time.GetUtcNow() + delay);
            });
        return first;
    }

    public void RecordSuccess(TableIdentifier table) => _state.TryRemove(table, out _);
}

/// <summary>One polling pass over all tracked tables, executed under the cluster lease.</summary>
public sealed class MssqlChangeTrackingPollJob(
    IMssqlChangeTrackingPoller poller,
    IOptionsMonitor<GatewayOptions> options,
    ILogger<MssqlChangeTrackingPollJob> logger,
    CdcPollFailureTracker? failureTracker = null) : IClusterScheduledJob
{
    private readonly CdcPollFailureTracker _tracker = failureTracker ?? new CdcPollFailureTracker();

    public async Task ExecuteAsync(ClusterJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tables = MssqlChangeTrackingHostedService.ParseTrackedTables(options.CurrentValue.MssqlChangeTracking.TrackedTables);
        if (tables.Count == 0)
        {
            logger.LogWarning("MSSQL Change Tracking is enabled but no valid tables are configured in TrackedTables.");
            return;
        }

        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();
            if (_tracker.ShouldSkip(table))
            {
                continue;
            }

            try
            {
                var processed = await poller.PollTableChangesAsync(table, context.FencingToken, ct).ConfigureAwait(false);
                _tracker.RecordSuccess(table);
                if (processed > 0)
                {
                    logger.LogDebug("Polled and dispatched {Count} CDC changes for table {Table}.", processed, table);
                }
            }
            catch (Exception ex) when (ex is CdcWatermarkExpiredException or CdcSourceConfigurationException)
            {
                // Persistent: retrying every cycle cannot help. Log once per streak and back off.
                if (_tracker.RecordFailure(table))
                {
                    logger.LogError(ex, "Change tracking of table {Table} is stopped until the cause is fixed; retrying with backoff.", table);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not FencingTokenRejectedException)
            {
                logger.LogError(ex, "Failed to poll change tracking for table {Table}.", table);
            }
        }
    }
}
