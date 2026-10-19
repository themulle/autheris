namespace Autheris.Application.Streaming.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// The data-free wake-up message of <see cref="CdcStreamSignal"/> (PLAN-CDC-99 D-A12, AC-14): stream id and head offset only,
/// never row data, tenant or table name.
/// </summary>
public sealed record CdcStreamAdvanced(string StreamId, long HeadOffset);

/// <summary>
/// Wake-up signal over <see cref="IDistributedClusterStateProvider"/> (node-local with the in-memory provider, Pub/Sub when cluster-wide).
/// Best effort: a lost or failed signal only adds latency because tail readers also poll the head offset.
/// </summary>
public sealed class CdcStreamSignal(IDistributedClusterStateProvider clusterState, ILogger<CdcStreamSignal> logger) : ICdcStreamSignal
{
    public const string Channel = "cdc:stream:advanced";

    public async ValueTask NotifyAdvancedAsync(CdcStreamId id, long headOffset, CancellationToken ct = default)
    {
        try
        {
            await clusterState.PublishEventAsync(Channel, new CdcStreamAdvanced(id.Value, headOffset), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never fails the producer: tail readers fall back to polling.
            logger.LogDebug(ex, "CDC stream wake-up signal could not be published.");
        }
    }

    public IAsyncDisposable Subscribe(Func<CdcStreamId, long, ValueTask> onAdvanced)
    {
        ArgumentNullException.ThrowIfNull(onAdvanced);
        return clusterState.SubscribeAsync<CdcStreamAdvanced>(Channel, async message =>
        {
            if (message is null || !CdcStreamId.TryParse(message.StreamId, out var id))
            {
                return;
            }

            try
            {
                await onAdvanced(id, message.HeadOffset).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "A CDC stream wake-up handler failed.");
            }
        });
    }
}
