namespace Autheris.Application.Streaming.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Hosting;
using Autheris.Application.State;
using Autheris.Application.Streaming.Crypto;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>Cluster-singleton retention sweep of the CDC streams (PLAN-CDC-99 D-A11), lease <c>cdc:stream:retention</c>.</summary>
public class CdcStreamRetentionService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<GatewayOptions> options,
    IDistributedClusterStateProvider clusterState,
    ILogger<CdcStreamRetentionService> logger,
    TimeProvider? timeProvider = null)
    : ClusterScheduledService<CdcStreamRetentionJob>(scopeFactory, clusterState, logger, timeProvider)
{
    protected override string LeaseKey => "cdc:stream:retention";

    protected override ClusterSchedule GetSchedule()
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.CurrentValue.CdcStream.RetentionSweepSeconds));
        return new ClusterSchedule(
            Enabled: true,
            Interval: interval,
            InitialDelay: TimeSpan.Zero,
            LeaseTtl: TimeSpan.FromSeconds(Math.Max(30, 3 * interval.TotalSeconds)),
            MaxJitter: TimeSpan.Zero);
    }
}

/// <summary>
/// One retention sweep. Per stream: <c>newTail = max(tail, first offset with produced &gt;= now - RetentionHours, head - RetentionMaxEvents + 1)</c>;
/// events below it are deleted in bounded, fenced chunks; payload DEKs no retained event references are deleted (crypto-shredding);
/// DEKs wrapped under a retired KEK are re-wrapped under the active one; images of disabled streams are purged. Retention never waits for
/// consumers: a slower consumer observes a gap.
/// </summary>
public sealed class CdcStreamRetentionJob(
    ICdcStreamStore store,
    ICdcStreamKeyStore keyStore,
    CdcStreamKeyring keyring,
    IOptionsMonitor<GatewayOptions> options,
    ILogger<CdcStreamRetentionJob> logger,
    TimeProvider? timeProvider = null) : IClusterScheduledJob
{
    private static readonly Counter<long> Purged = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_cdc_stream_purged_total", description: "CDC stream events removed by retention");

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task ExecuteAsync(ClusterJobContext context, CancellationToken ct)
    {
        foreach (var stream in await store.ListStreamsAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SweepAsync(stream, context.FencingToken, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or FencingTokenRejectedException))
            {
                // One failing stream must not block the others; only the stream id and the exception type are logged.
                logger.LogWarning("CDC retention of stream {StreamId} failed: {ErrorType}.", stream.Id.Value, ex.GetType().Name);
            }
        }
    }

    internal async Task SweepAsync(CdcStreamDescriptor stream, long fencingToken, CancellationToken ct)
    {
        var (hours, maxEvents) = Limits(stream.Table);
        var batch = Math.Max(1, options.CurrentValue.CdcStream.RetentionDeleteBatch);
        var cutoff = _time.GetUtcNow() - TimeSpan.FromHours(hours);

        var newTail = stream.TailOffset;
        if (stream.HeadOffset >= stream.TailOffset)
        {
            newTail = Math.Max(newTail, await FirstOffsetAtOrAfterAsync(stream, cutoff, ct).ConfigureAwait(false));
            if (maxEvents > 0)
            {
                newTail = Math.Max(newTail, stream.HeadOffset - maxEvents + 1);
            }

            newTail = Math.Min(newTail, stream.HeadOffset + 1);
        }

        if (newTail > stream.TailOffset)
        {
            long total = 0;
            while (true)
            {
                var deleted = await store.PurgeAsync(stream.Id, newTail, batch, fencingToken, ct).ConfigureAwait(false);
                total += deleted;
                if (deleted < batch)
                {
                    break;
                }
            }

            if (total > 0)
            {
                Purged.Add(total, new KeyValuePair<string, object?>("stream", stream.Id.Value));
            }
        }

        if (stream.Status == CdcStreamStatus.Disabled)
        {
            while (await store.PurgeImagesAsync(stream.Id, batch, ct).ConfigureAwait(false) >= batch)
            {
            }
        }

        // Crypto-shredding: a payload DEK that no retained event references becomes unrecoverable.
        await keyStore.DeleteUnreferencedPayloadKeysAsync(stream.Id, ct).ConfigureAwait(false);
        await keyring.RewrapAsync(stream.Id, ct).ConfigureAwait(false);
    }

    private (int Hours, long MaxEvents) Limits(TableIdentifier table)
    {
        var o = options.CurrentValue;
        var hours = o.CdcStream.RetentionHours;
        var max = o.CdcStream.RetentionMaxEvents;
        foreach (var t in o.MssqlChangeTracking.Tables)
        {
            if (TableIdentifier.TryParse(t.Table, out var id) && id.Equals(table))
            {
                hours = t.RetentionHours ?? hours;
                max = t.RetentionMaxEvents ?? max;
                break;
            }
        }

        return (Math.Max(1, hours), max);
    }

    /// <summary>Binary search over the dense, time-ordered offsets (one single-row read per probe, no bulk payload transfer).</summary>
    private async Task<long> FirstOffsetAtOrAfterAsync(CdcStreamDescriptor stream, DateTimeOffset cutoff, CancellationToken ct)
    {
        long lo = stream.TailOffset;
        long hi = stream.HeadOffset + 1; // answer in [lo, hi]; hi = none is recent enough
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var read = await store.ReadAsync(stream.Id, mid, 1, ct).ConfigureAwait(false);
            if (read.Events.Count == 0 || read.Events[0].ProducedAt < cutoff)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
