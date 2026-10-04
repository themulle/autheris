namespace Autheris.Application.Events.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// F-EVT-01: Dispatches signed CloudEvents v1.0 payloads to outbound webhook subscribers.
/// </summary>
public interface ICloudEventWebhookDispatcher
{
    ValueTask<CloudEventDeliveryResult> DispatchAsync(
        CloudEventWebhookSubscription subscription,
        CloudEventEnvelope envelope,
        CancellationToken ct = default);
}
