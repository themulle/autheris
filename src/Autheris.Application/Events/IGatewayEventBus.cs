namespace Autheris.Application.Events;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Events;

/// <summary>
/// In-process event bus for publishing and subscribing to gateway domain and operational events.
/// </summary>
public interface IGatewayEventBus
{
    /// <summary>
    /// Publishes a domain event to all registered in-process listeners.
    /// Execution is fault-isolated; failing listeners do not abort execution of other listeners.
    /// </summary>
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IGatewayEvent;

    /// <summary>
    /// Subscribes a strongly-typed event handler to a specific event type.
    /// </summary>
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : IGatewayEvent;

    /// <summary>
    /// Subscribes an event handler to events of a specific runtime type or any type assignable to it.
    /// </summary>
    IDisposable Subscribe(Type eventType, Func<IGatewayEvent, CancellationToken, Task> handler);
}
