namespace Autheris.Extensions.Cdc;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Hosting;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// REQ-CDC-99 D-A2: the change tracking producer. One fenced lease loop per tracked table (key <c>cdc:mssql:poll:{domain}.{schema}.{table}</c>,
/// TTL <c>max(15 s, 3 x interval)</c>, per-table interval), so replicas share the tables and one slow table never delays another. The lease
/// token is the fencing token of the append transaction; a lost lease cancels the poll. Persistent failures (failed activation, invalid
/// configuration) back off exponentially and are logged once per streak with the stable code only.
/// </summary>
public sealed class CdcProducerService : ClusterPartitionedService
{
    private readonly IMssqlChangeTrackingPoller _poller;
    private readonly ICdcStreamStore _store;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<CdcProducerService> _logger;
    private readonly CdcPollFailureTracker _tracker;

    public CdcProducerService(
        IMssqlChangeTrackingPoller poller,
        ICdcStreamStore store,
        IOptionsMonitor<GatewayOptions> options,
        IDistributedClusterStateProvider clusterState,
        IHostEnvironment environment,
        ILogger<CdcProducerService> logger,
        CdcPollFailureTracker? failureTracker = null,
        TimeProvider? timeProvider = null)
        : base(clusterState, logger, timeProvider)
    {
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tracker = failureTracker ?? new CdcPollFailureTracker(timeProvider);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // The stream and its watermark must survive a restart; only Development may run without a durable store.
        if (_options.CurrentValue.MssqlChangeTracking.Enabled && !_store.IsDurable && !_environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "MSSQL Change Tracking requires a durable CDC stream store outside Development (the governance database store is registered by the host).");
        }

        return base.StartAsync(cancellationToken);
    }

    protected override IReadOnlyList<ClusterPartition> GetPartitions()
    {
        var cdc = _options.CurrentValue.MssqlChangeTracking;
        if (!cdc.Enabled)
        {
            return [];
        }

        return CdcTableSettings.Resolve(cdc)
            .Select(s => new ClusterPartition(s.PartitionKey, new ClusterSchedule(true, s.PollInterval, TimeSpan.Zero, s.LeaseTtl, TimeSpan.Zero)))
            .ToList();
    }

    /// <summary>One guarded cycle of every tracked table, sequentially. A deterministic seam for tests (no timers).</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        foreach (var partition in GetPartitions())
        {
            await RunPartitionCycleAsync(partition, ct).ConfigureAwait(false);
        }
    }

    protected override async Task ExecutePartitionAsync(ClusterPartition partition, ClusterJobContext context, CancellationToken ct)
    {
        var settings = CdcTableSettings.Resolve(_options.CurrentValue.MssqlChangeTracking).FirstOrDefault(s => s.PartitionKey == partition.Key);
        if (settings is null)
        {
            return; // removed between reconcile and run
        }

        var table = settings.Table;
        if (_tracker.ShouldSkip(table))
        {
            return;
        }

        try
        {
            var appended = await _poller.PollTableChangesAsync(table, context.FencingToken, ct).ConfigureAwait(false);
            _tracker.RecordSuccess(table);
            if (appended > 0)
            {
                _logger.LogDebug("Appended {Count} CDC events for table {Table}.", appended, table);
            }
        }
        catch (Exception ex) when (ex is CdcWatermarkExpiredException or CdcSourceConfigurationException)
        {
            // Persistent: retrying every cycle cannot help. The message is a stable code, never data. Log once per streak and back off.
            if (_tracker.RecordFailure(table))
            {
                _logger.LogError("Change tracking of table {Table} is stopped until the cause is fixed ({Code}); retrying with backoff.", table, ex.Message);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FencingTokenRejectedException)
        {
            _logger.LogError(ex, "Failed to poll change tracking for table {Table}.", table);
        }
    }
}
