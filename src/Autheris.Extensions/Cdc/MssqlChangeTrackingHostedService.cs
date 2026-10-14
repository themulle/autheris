namespace Autheris.Extensions.Cdc;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Hosting;
using Autheris.Application.State;
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

    protected override string LeaseKey => "cdc:mssql:poll";

    protected override ClusterSchedule GetSchedule()
    {
        var cdc = options.CurrentValue.MssqlChangeTracking;
        var interval = TimeSpan.FromMilliseconds(Math.Max(250, cdc.PollingIntervalMilliseconds));
        var enabled = cdc.Enabled && ParseTrackedTables(cdc.TrackedTables).Count > 0;
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

/// <summary>One polling pass over all tracked tables, executed under the cluster lease.</summary>
public sealed class MssqlChangeTrackingPollJob(
    IMssqlChangeTrackingPoller poller,
    IOptionsMonitor<GatewayOptions> options,
    ILogger<MssqlChangeTrackingPollJob> logger) : IClusterScheduledJob
{
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

            try
            {
                var processed = await poller.PollTableChangesAsync(table, context.FencingToken, ct).ConfigureAwait(false);
                if (processed > 0)
                {
                    logger.LogDebug("Polled and dispatched {Count} CDC changes for table {Table}.", processed, table);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not FencingTokenRejectedException)
            {
                logger.LogError(ex, "Failed to poll change tracking for table {Table}.", table);
            }
        }
    }
}
