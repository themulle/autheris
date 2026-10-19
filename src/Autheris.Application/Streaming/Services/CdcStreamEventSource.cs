namespace Autheris.Application.Streaming.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// I1 compatibility source of GraphQL table subscriptions (PLAN-CDC-99 D-A13): the events of MSSQL-tracked tables come from the durable
/// stream through <see cref="ICdcStreamTailHub"/>, every other source keeps using the channel. The merged events still pass the subscriber's
/// own authorisation and masking before they leave the subscription (SEC-CDC99-05).
/// </summary>
public static class CdcStreamEventSource
{
    /// <summary>Streams a topic (<c>cdc_all</c>, <c>cdc_{schema.table}</c> or <c>cdc_{table}</c>) must read from; empty when none is stream-backed.</summary>
    public static async Task<IReadOnlyList<CdcStreamId>> ResolveStreamsAsync(ICdcStreamStore store, string topic, string allTopic, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        var streams = await store.ListStreamsAsync(ct).ConfigureAwait(false);
        return
        [
            .. streams
                .Where(s => s.Status == CdcStreamStatus.Active && s.Source == CdcStreamCompatFeed.MssqlSource && Matches(s.Table, topic, allTopic))
                .Select(s => s.Id)
        ];
    }

    public static bool Matches(TableIdentifier table, string topic, string allTopic) =>
        string.Equals(topic, allTopic, StringComparison.Ordinal)
        || string.Equals(topic, $"cdc_{table.ToQualifiedName().ToLowerInvariant()}", StringComparison.Ordinal)
        || string.Equals(topic, $"cdc_{table.TableName.ToLowerInvariant()}", StringComparison.Ordinal);

    /// <summary>Merges the channel events with the mapped stream deliveries. A failing source completes the merge with its exception.</summary>
    public static async IAsyncEnumerable<CdcEvent> MergeAsync(
        IAsyncEnumerable<CdcEvent> channelEvents, IReadOnlyList<CdcStreamId> streams, ICdcStreamTailHub hub, int capacity,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channelEvents);
        ArgumentNullException.ThrowIfNull(hub);
        var merged = Channel.CreateBounded<CdcEvent>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var pumps = new List<Task>
        {
            PumpAsync(channelEvents, merged.Writer, cts)
        };
        pumps.AddRange(streams.Select(id => PumpAsync(PumpStreamAsync(hub, id, cts.Token), merged.Writer, cts)));
        _ = Task.WhenAll(pumps).ContinueWith(
            _ => merged.Writer.TryComplete(),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            await foreach (var evt in merged.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
    }

    private static async IAsyncEnumerable<CdcEvent> PumpStreamAsync(ICdcStreamTailHub hub, CdcStreamId id, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var delivery in hub.AttachAsync(id, null, ct).ConfigureAwait(false))
        {
            foreach (var evt in CdcStreamCompatFeed.Map(delivery))
            {
                yield return evt;
            }
        }
    }

    private static async Task PumpAsync(IAsyncEnumerable<CdcEvent> source, ChannelWriter<CdcEvent> writer, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await foreach (var evt in source.WithCancellation(ct).ConfigureAwait(false))
            {
                await writer.WriteAsync(evt, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Subscription ended.
        }
        catch (Exception ex)
        {
            // The first failing source ends the whole merge with its exception; the other sources are stopped.
            writer.TryComplete(ex);
            await cts.CancelAsync().ConfigureAwait(false);
        }
    }
}
