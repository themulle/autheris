namespace Autheris.Extensions.Cdc;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Autheris.Application.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Background worker service polling MSSQL Change Tracking tables on a configurable interval.
/// Zero-Kafka Realtime CDC Engine (F-CDC-02).
/// </summary>
public sealed class MssqlChangeTrackingHostedService : BackgroundService
{
    private readonly IMssqlChangeTrackingPoller _poller;
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly ILogger<MssqlChangeTrackingHostedService> _logger;
    private readonly Autheris.Application.State.IDistributedClusterStateProvider? _clusterState;

    public MssqlChangeTrackingHostedService(
        IMssqlChangeTrackingPoller poller,
        IOptions<GatewayOptions> gatewayOptions,
        ILogger<MssqlChangeTrackingHostedService> logger,
        Autheris.Application.State.IDistributedClusterStateProvider? clusterState = null)
    {
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clusterState = clusterState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _gatewayOptions.Value.MssqlChangeTracking;
        if (!options.Enabled)
        {
            _logger.LogInformation("MSSQL Change Tracking ingestion is disabled.");
            return;
        }

        var trackedTableIds = ParseTrackedTables(options.TrackedTables);
        if (trackedTableIds.Count == 0)
        {
            _logger.LogWarning("MSSQL Change Tracking is enabled but no valid tables are configured in TrackedTables.");
            return;
        }

        _logger.LogInformation(
            "Starting MSSQL Change Tracking background poller for {Count} tables with interval {Interval}ms.",
            trackedTableIds.Count, options.PollingIntervalMilliseconds);

        var interval = Math.Max(250, options.PollingIntervalMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                IAsyncDisposable? pollLock = null;
                if (_clusterState != null)
                {
                    var leaseDuration = TimeSpan.FromMilliseconds(Math.Max(5000, interval * 3));
                    pollLock = await _clusterState.TryAcquireLockAsync("cdc:mssql:poll", leaseDuration, stoppingToken).ConfigureAwait(false);
                    if (pollLock == null)
                    {
                        _logger.LogDebug("MSSQL Change Tracking polling lock held by another cluster replica; skipping cycle.");
                        try
                        {
                            await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        continue;
                    }
                }

                await using (pollLock)
                {
                    foreach (var table in trackedTableIds)
                    {
                        if (stoppingToken.IsCancellationRequested) break;

                        try
                        {
                            var processed = await _poller.PollTableChangesAsync(table, stoppingToken).ConfigureAwait(false);
                            if (processed > 0)
                            {
                                _logger.LogDebug("Polled and dispatched {Count} CDC changes for table {Table}.", processed, table);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogError(ex, "Failed to poll change tracking for table {Table}.", table);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Unexpected error in MSSQL Change Tracking loop.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("MSSQL Change Tracking background poller stopped.");
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
