namespace Autheris.Application.Common.Interfaces;

using System;

/// <summary>
/// Optional capability of an <see cref="IEventBus"/>: raised with the logical channel name when a received message could not be
/// delivered to its subscriber (deserialization failure, null payload or handler exception). A cache that relies on invalidation messages
/// uses it to drop its entries, because the lost message may have been an invalidation (fail closed).
/// </summary>
public interface IEventBusDeliveryFailures
{
    event Action<string>? DeliveryFailed;
}
