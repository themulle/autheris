namespace Autheris.Application.Streaming.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Crypto;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// I1 compatibility feed (PLAN-CDC-99 D-A13, SEC-CDC99-18): keeps the existing at-most-once webhook behaviour working from the durable
/// stream. Decoded stream deliveries of MSSQL-tracked tables are mapped to <see cref="CdcEvent"/> (opaque id, tenant attribution) and handed to
/// a sink. It runs only on the replica holding the lease <c>cdc:webhook:compat</c> (outbox dedupe on <c>event_id</c> covers a takeover) and is
/// replaced by durable consumers in I2. Without a registered stream store it is inert.
/// </summary>
public sealed class CdcStreamCompatFeed(IServiceProvider services, TimeProvider time, ILogger<CdcStreamCompatFeed> logger)
{
    public const string LeaseKey = "cdc:webhook:compat";
    public const string MssqlSource = "mssql_ct";
    private static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maps a stream delivery to channel-style events: <c>[0]</c> for the event tenant and, when a row moved between tenants (SEC-CDC99-01),
    /// a delete for the previous tenant. A before-image is only ever attached to an event of its own tenant. Control events, unresolved and
    /// shared-scope changes map to nothing.
    /// </summary>
    public static IReadOnlyList<CdcEvent> Map(CdcStreamDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var record = delivery.Record;
        if (delivery.Change is not { } change || record.Kind != CdcStreamEventKind.Change || record.Operation is not { } op)
        {
            return [];
        }

        if (record.TenantScope is not (CdcTenantScope.Row or CdcTenantScope.Owner) || string.IsNullOrWhiteSpace(record.TenantId))
        {
            return [];
        }

        var id = CdcEventId.Format(record.StreamId, record.Offset);
        var moved = !string.IsNullOrEmpty(record.PriorTenantId) && !string.Equals(record.PriorTenantId, record.TenantId, StringComparison.Ordinal);
        var events = new List<CdcEvent>(2)
        {
            new(id, delivery.Table, moved && op == CdcOperation.Update ? CdcOperation.Insert : op, record.TenantId,
                moved ? null : change.Before, change.After, record.ProducedAt)
        };

        if (moved && op == CdcOperation.Update && change.Before is not null)
        {
            events.Add(new CdcEvent(id, delivery.Table, CdcOperation.Delete, record.PriorTenantId, change.Before, null, record.ProducedAt));
        }

        return events;
    }

    /// <summary>Runs until cancelled: holds the compat lease, attaches to every active MSSQL stream and feeds the sink.</summary>
    public async Task RunAsync(Func<CdcEvent, CancellationToken, Task> sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var store = services.GetService<ICdcStreamStore>();
        var hub = services.GetService<ICdcStreamTailHub>();
        var cluster = services.GetService<IDistributedClusterStateProvider>();
        if (store is null || hub is null || cluster is null)
        {
            logger.LogDebug("The CDC compatibility feed is inert: the stream services are not registered.");
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                IDistributedLease? lease = null;
                try
                {
                    lease = await cluster.TryAcquireLeaseAsync(LeaseKey, LeaseTtl, ct).ConfigureAwait(false);
                }
                catch (ClusterStateUnavailableException)
                {
                    // Never run unguarded: without the lease the feed idles.
                }

                if (lease is null)
                {
                    await Task.Delay(RetryDelay, time, ct).ConfigureAwait(false);
                    continue;
                }

                await using (lease.ConfigureAwait(false))
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.LeaseLost);
                    await RunLeasedAsync(store, hub, sink, linked.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    private async Task RunLeasedAsync(ICdcStreamStore store, ICdcStreamTailHub hub, Func<CdcEvent, CancellationToken, Task> sink, CancellationToken ct)
    {
        var running = new Dictionary<string, Task>(StringComparer.Ordinal);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var done in running.Where(kv => kv.Value.IsCompleted).Select(kv => kv.Key).ToList())
                {
                    running.Remove(done);
                }

                IReadOnlyList<CdcStreamDescriptor> streams;
                try
                {
                    streams = await store.ListStreamsAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("The CDC compatibility feed could not list streams ({ErrorType}).", ex.GetType().Name);
                    streams = [];
                }

                foreach (var stream in streams)
                {
                    if (stream.Status == CdcStreamStatus.Active && stream.Source == MssqlSource && !running.ContainsKey(stream.Id.Value))
                    {
                        running[stream.Id.Value] = PumpAsync(hub, stream.Id, sink, ct);
                    }
                }

                await Task.Delay(RescanInterval, time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping (shutdown or lost lease).
        }
        finally
        {
            try
            {
                await Task.WhenAll(running.Values).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Pumps end with the token.
            }
        }
    }

    private async Task PumpAsync(ICdcStreamTailHub hub, CdcStreamId id, Func<CdcEvent, CancellationToken, Task> sink, CancellationToken ct)
    {
        try
        {
            await foreach (var delivery in hub.AttachAsync(id, null, ct).ConfigureAwait(false))
            {
                foreach (var evt in Map(delivery))
                {
                    await sink(evt, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception ex)
        {
            // Type only. The rescan re-attaches; events during the gap are lost (at-most-once, as documented for the I1 feed).
            logger.LogWarning("CDC compatibility feed of stream {StreamId} stopped ({ErrorType}).", id.Value, ex.GetType().Name);
        }
    }
}
