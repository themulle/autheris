namespace Autheris.Application.Events;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Events;
using Microsoft.Extensions.Logging;

/// <summary>
/// Thread-safe in-memory implementation of the gateway domain event bus.
/// Guarantees subscriber fault isolation and fail-safe execution.
/// </summary>
public sealed class InMemoryGatewayEventBus : IGatewayEventBus
{
    private readonly ILogger<InMemoryGatewayEventBus> _logger;
    private readonly ConcurrentDictionary<Type, List<SubscriptionRecord>> _subscriptions = new();
    private readonly object _lock = new();

    private sealed record SubscriptionRecord(
        Guid Id,
        Type EventType,
        Func<IGatewayEvent, CancellationToken, Task> Handler);

    public InMemoryGatewayEventBus(ILogger<InMemoryGatewayEventBus> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IGatewayEvent
    {
        ArgumentNullException.ThrowIfNull(@event);

        List<SubscriptionRecord> handlersToInvoke = [];

        var concreteType = @event.GetType();

        lock (_lock)
        {
            foreach (var kvp in _subscriptions)
            {
                if (kvp.Key.IsAssignableFrom(concreteType))
                {
                    handlersToInvoke.AddRange(kvp.Value);
                }
            }
        }

        if (handlersToInvoke.Count == 0)
        {
            _logger.LogTrace("No subscribers registered for event {EventType} ({EventId})", @event.EventType, @event.EventId);
            return;
        }

        _logger.LogDebug(
            "Publishing event {EventType} ({EventId}) to {SubscriberCount} subscriber(s)",
            @event.EventType, @event.EventId, handlersToInvoke.Count);

        var tasks = handlersToInvoke.Select(sub => InvokeHandlerSafelyAsync(sub, @event, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : IGatewayEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        Task UniversalHandler(IGatewayEvent ev, CancellationToken ct)
        {
            if (ev is TEvent typedEvent)
            {
                return handler(typedEvent, ct);
            }
            return Task.CompletedTask;
        }

        return Subscribe(typeof(TEvent), UniversalHandler);
    }

    public IDisposable Subscribe(Type eventType, Func<IGatewayEvent, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(handler);

        if (!typeof(IGatewayEvent).IsAssignableFrom(eventType))
        {
            throw new ArgumentException(
                $"Type {eventType.FullName} does not implement {nameof(IGatewayEvent)}.",
                nameof(eventType));
        }

        var record = new SubscriptionRecord(Guid.NewGuid(), eventType, handler);

        lock (_lock)
        {
            var list = _subscriptions.GetOrAdd(eventType, _ => []);
            list.Add(record);
        }

        return new SubscriptionToken(() =>
        {
            lock (_lock)
            {
                if (_subscriptions.TryGetValue(eventType, out var list))
                {
                    list.RemoveAll(r => r.Id == record.Id);
                    if (list.Count == 0)
                    {
                        _subscriptions.TryRemove(eventType, out _);
                    }
                }
            }
        });
    }

    private async Task InvokeHandlerSafelyAsync(SubscriptionRecord sub, IGatewayEvent @event, CancellationToken ct)
    {
        try
        {
            await sub.Handler(@event, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Propagate cancellation or ignore depending on shutdown
            _logger.LogWarning("Event handler for {EventType} cancelled", @event.EventType);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Subscriber error handling event {EventType} ({EventId}) in subscription {SubscriptionId}",
                @event.EventType, @event.EventId, sub.Id);
        }
    }

    private sealed class SubscriptionToken : IDisposable
    {
        private Action? _unsubscribe;

        public SubscriptionToken(Action unsubscribe)
        {
            _unsubscribe = unsubscribe;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
        }
    }
}
