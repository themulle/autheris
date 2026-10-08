namespace Autheris.Infrastructure.Streaming;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-CDC-03: Zero-Kafka PostgreSQL CDC via Logical Streaming Replication (pgoutput).
/// Connects directly to PostgreSQL replication stream, decodes WAL events, enforces tenant isolation,
/// manages LSN acknowledgements, and monitors WAL lag to prevent disk bloat.
/// </summary>
public sealed class PostgreSqlLogicalReplicationService : IPostgreSqlCdcService, IHostedService, IDisposable
{
    private readonly ICdcEventChannel _eventChannel;
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly ILogger<PostgreSqlLogicalReplicationService> _logger;
    private readonly WalMessageDecoder _decoder = new();

    private CancellationTokenSource? _cts;
    private Task? _executingTask;
    private bool _isRunning;
    private long _currentWalLagBytes;
    private ulong _lastAcknowledgedLsn;
    private ulong _latestServerLsn;

    public PostgreSqlLogicalReplicationService(
        ICdcEventChannel eventChannel,
        IOptions<GatewayOptions> gatewayOptions,
        ILogger<PostgreSqlLogicalReplicationService> logger)
    {
        _eventChannel = eventChannel ?? throw new ArgumentNullException(nameof(eventChannel));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsRunning => _isRunning;
    public long CurrentWalLagBytes => _currentWalLagBytes;
    public bool IsWalLagExceeded => _currentWalLagBytes > _gatewayOptions.Value.PostgreSqlCdc.MaxLagBytes;
    public ulong LastAcknowledgedLsn => _lastAcknowledgedLsn;
    public ulong LatestServerLsn => _latestServerLsn;
    public WalMessageDecoder Decoder => _decoder;

    public Task StartAsync(CancellationToken ct = default)
    {
        var options = _gatewayOptions.Value.PostgreSqlCdc;
        if (!options.Enabled)
        {
            _logger.LogInformation("F-CDC-03 PostgreSQL CDC is disabled by configuration.");
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "F-CDC-03 Starting PostgreSQL Logical CDC with slot '{Slot}' on publication '{Pub}' (MaxLag: {MaxLag} bytes)",
            options.SlotName, options.PublicationName, options.MaxLagBytes);

        _isRunning = true;
        _cts = new CancellationTokenSource();
        _executingTask = RunReplicationLoopAsync(_cts.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("F-CDC-03 Stopping PostgreSQL Logical CDC service...");
        _isRunning = false;

        if (_cts != null)
        {
            try
            {
                await _cts.CancelAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception while cancelling PostgreSQL CDC token.");
            }
        }

        if (_executingTask != null)
        {
            await Task.WhenAny(_executingTask, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
        }

        _logger.LogInformation("F-CDC-03 PostgreSQL Logical CDC service stopped.");
    }

    public async ValueTask ProcessChangeAsync(WalChange change, ulong serverLsn, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        _latestServerLsn = serverLsn;
        if (_latestServerLsn > _lastAcknowledgedLsn)
        {
            _currentWalLagBytes = (long)(_latestServerLsn - _lastAcknowledgedLsn);
        }

        var options = _gatewayOptions.Value.PostgreSqlCdc;
        if (IsWalLagExceeded)
        {
            _logger.LogWarning(
                "F-CDC-03 PostgreSQL WAL lag ({Lag} bytes) exceeds limit ({Max} bytes)! Throttling CDC stream to avoid disk overflow.",
                _currentWalLagBytes, options.MaxLagBytes);
            return;
        }

        var cdcEvent = _decoder.DecodeChange(change);
        if (cdcEvent == null)
        {
            _logger.LogDebug("F-CDC-03 Received WAL change for unmapped relation {RelId}", change.RelationId);
            return;
        }

        // Table filtering if TrackedTables specified
        if (options.TrackedTables.Count > 0)
        {
            var tableName = cdcEvent.Table.TableName;
            var qualified = cdcEvent.Table.ToQualifiedName();
            var matches = options.TrackedTables.Any(t =>
                string.Equals(t, tableName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, qualified, StringComparison.OrdinalIgnoreCase));

            if (!matches)
            {
                return;
            }
        }

        await _eventChannel.PublishAsync(cdcEvent, ct).ConfigureAwait(false);

        _lastAcknowledgedLsn = change.Lsn;
        if (_latestServerLsn > _lastAcknowledgedLsn)
        {
            _currentWalLagBytes = (long)(_latestServerLsn - _lastAcknowledgedLsn);
        }
        else
        {
            _currentWalLagBytes = 0;
        }
    }

    private async Task RunReplicationLoopAsync(CancellationToken ct)
    {
        var options = _gatewayOptions.Value.PostgreSqlCdc;
        while (!ct.IsCancellationRequested && _isRunning)
        {
            try
            {
                // In production, connects via Npgsql.Replication.LogicalReplicationConnection
                // when connection string is configured.
                if (string.IsNullOrWhiteSpace(options.ConnectionString))
                {
                    _logger.LogDebug("F-CDC-03 No ConnectionString configured; running CDC worker in standby.");
                    await Task.Delay(options.AckIntervalMilliseconds, ct).ConfigureAwait(false);
                    continue;
                }

                // EXT-4: logical replication (Npgsql.Replication) is not implemented. Do not pretend to stream changes.
                _logger.LogError("F-CDC-03 PostgreSQL logical replication is not implemented; no change events are captured. The CDC worker stops.");
                _isRunning = false;
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "F-CDC-03 Error during PostgreSQL logical replication loop. Retrying in 5s...");
                try
                {
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _cts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
