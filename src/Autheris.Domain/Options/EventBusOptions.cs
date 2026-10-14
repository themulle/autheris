namespace Autheris.Domain.Options;

/// <summary>How the in-process gateway event bus hands events to subscribers.</summary>
public enum EventBusDeliveryMode
{
    /// <summary>
    /// Every subscription owns a bounded queue and a single reader task. <c>PublishAsync</c> only enqueues and
    /// returns immediately (at-most-once, per-subscriber ordered, lossy under overload).
    /// </summary>
    Queued = 0,

    /// <summary>
    /// <c>PublishAsync</c> awaits all handlers (bounded by the per-handler timeout). Intended for tests only;
    /// rejected outside Development.
    /// </summary>
    Inline = 1,
}

/// <summary>
/// Options of the in-process event bus. Bound to the configuration section <see cref="SectionName"/>.
/// </summary>
public sealed class EventBusOptions
{
    public const string SectionName = "Gateway:Events";

    /// <summary>Default capacity of each subscriber queue.</summary>
    public const int DefaultSubscriberQueueCapacity = 1024;

    /// <summary>Capacity of each subscriber queue; when full the oldest event is dropped.</summary>
    public int SubscriberQueueCapacity { get; init; } = DefaultSubscriberQueueCapacity;

    /// <summary>Delivery mode. Default: <see cref="EventBusDeliveryMode.Queued"/>.</summary>
    public EventBusDeliveryMode DeliveryMode { get; init; } = EventBusDeliveryMode.Queued;
}
