namespace Autheris.Application.Events;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Events;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;

/// <summary>
/// Thread-safe in-memory implementation of the gateway domain event bus.
/// In <see cref="EventBusDeliveryMode.Queued"/> mode (default) every subscription owns a bounded queue
/// (<see cref="EventBusOptions.SubscriberQueueCapacity"/>, drop-oldest) and a single reader task, so
/// <see cref="PublishAsync{TEvent}"/> never waits for subscribers. Delivery is in-process, at-most-once,
/// ordered per subscriber and lossy under overload. Subscriber faults and timeouts (SR-M-13) are isolated.
/// Security decisions must never depend on this bus.
/// </summary>
public sealed class InMemoryGatewayEventBus : IGatewayEventBus, IGatewayEventBusDrain, IDisposable
{
    private readonly ILogger<InMemoryGatewayEventBus> _logger;
    private readonly TimeSpan _handlerTimeout;
    private readonly EventBusDeliveryMode _mode;
    private readonly int _capacity;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<Type, Subscription[]> _matchCache = new();
    private Subscription[] _all = [];
    private long _pending;

    /// <summary>Default upper bound for a single subscriber invocation (SR-M-13).</summary>
    public static readonly TimeSpan DefaultHandlerTimeout = TimeSpan.FromSeconds(10);

    private sealed class Subscription
    {
        public Subscription(Guid id, Type eventType, Func<IGatewayEvent, CancellationToken, Task> handler)
        {
            Id = id;
            EventType = eventType;
            Handler = handler;
        }

        public Guid Id { get; }
        public Type EventType { get; }
        public Func<IGatewayEvent, CancellationToken, Task> Handler { get; }
        public Channel<IGatewayEvent>? Queue { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public Task? Reader { get; set; }
    }

    public InMemoryGatewayEventBus(ILogger<InMemoryGatewayEventBus> logger, TimeSpan? handlerTimeout = null)
        : this(logger, new EventBusOptions(), handlerTimeout)
    {
    }

    public InMemoryGatewayEventBus(
        ILogger<InMemoryGatewayEventBus> logger,
        EventBusOptions options,
        TimeSpan? handlerTimeout = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(options);
        _handlerTimeout = handlerTimeout is { } t && t > TimeSpan.Zero ? t : DefaultHandlerTimeout;
        _mode = options.DeliveryMode;
        _capacity = Math.Max(1, options.SubscriberQueueCapacity);
    }

    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IGatewayEvent
    {
        ArgumentNullException.ThrowIfNull(@event);

        var targets = GetMatching(@event.GetType());
        if (targets.Length == 0)
        {
            _logger.LogTrace("No subscribers registered for event {EventType} ({EventId})", @event.EventType, @event.EventId);
            return Task.CompletedTask;
        }

        _logger.LogDebug(
            "Publishing event {EventType} ({EventId}) to {SubscriberCount} subscriber(s)",
            @event.EventType, @event.EventId, targets.Length);

        if (_mode == EventBusDeliveryMode.Inline)
        {
            return PublishInlineAsync(targets, @event, ct);
        }

        IGatewayEvent ev = @event;
        foreach (var sub in targets)
        {
            var queue = sub.Queue!;
            Interlocked.Increment(ref _pending);
            if (!queue.Writer.TryWrite(ev))
            {
                // Subscription was disposed concurrently.
                Interlocked.Decrement(ref _pending);
            }
        }

        return Task.CompletedTask;
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

        var sub = new Subscription(Guid.NewGuid(), eventType, handler);

        if (_mode == EventBusDeliveryMode.Queued)
        {
            sub.Queue = Channel.CreateBounded<IGatewayEvent>(
                new BoundedChannelOptions(_capacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                },
                OnItemDropped);
            sub.Cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            sub.Reader = Task.Run(() => RunReaderAsync(sub));
        }

        lock (_lock)
        {
            var next = new Subscription[_all.Length + 1];
            Array.Copy(_all, next, _all.Length);
            next[^1] = sub;
            _all = next;
            _matchCache.Clear();
        }

        return new SubscriptionToken(() => Unsubscribe(sub));
    }

    public async Task DrainAsync(CancellationToken ct = default)
    {
        while (Interlocked.Read(ref _pending) > 0)
        {
            await Task.Delay(1, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        Subscription[] snapshot;
        lock (_lock)
        {
            snapshot = _all;
            _all = [];
            _matchCache.Clear();
        }

        _lifetime.Cancel();
        foreach (var sub in snapshot)
        {
            sub.Queue?.Writer.TryComplete();
        }
    }

    private Subscription[] GetMatching(Type concreteType)
    {
        if (_matchCache.TryGetValue(concreteType, out var cached))
        {
            return cached;
        }

        // Computed under the lock so a concurrent subscribe/unsubscribe (which clears the cache under the same
        // lock) can never leave a stale entry behind.
        lock (_lock)
        {
            if (_matchCache.TryGetValue(concreteType, out cached))
            {
                return cached;
            }

            var matches = new List<Subscription>();
            foreach (var sub in _all)
            {
                if (sub.EventType.IsAssignableFrom(concreteType))
                {
                    matches.Add(sub);
                }
            }

            var array = matches.ToArray();
            _matchCache[concreteType] = array;
            return array;
        }
    }

    private void Unsubscribe(Subscription sub)
    {
        lock (_lock)
        {
            var list = new List<Subscription>(_all);
            if (list.RemoveAll(s => s.Id == sub.Id) > 0)
            {
                _all = list.ToArray();
            }

            _matchCache.Clear();
        }

        sub.Cts?.Cancel();
        sub.Queue?.Writer.TryComplete();
    }

    private void OnItemDropped(IGatewayEvent dropped)
    {
        Interlocked.Decrement(ref _pending);
        GatewayDiagnostics.GatewayEventBusDroppedCounter.Add(
            1, new KeyValuePair<string, object?>("event_type", dropped.EventType));
        _logger.LogWarning(
            "Event bus subscriber queue full; dropped oldest event {EventType} ({EventId})",
            dropped.EventType, dropped.EventId);
    }

    private async Task RunReaderAsync(Subscription sub)
    {
        var token = sub.Cts!.Token;
        var reader = sub.Queue!.Reader;
        try
        {
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var ev))
                {
                    try
                    {
                        await InvokeHandlerSafelyAsync(sub, ev, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pending);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Bus disposed or subscription removed.
        }
        finally
        {
            // Account for events that were never handled.
            while (reader.TryRead(out _))
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    private async Task PublishInlineAsync(Subscription[] targets, IGatewayEvent @event, CancellationToken ct)
    {
        var tasks = new Task[targets.Length];
        for (var i = 0; i < targets.Length; i++)
        {
            tasks[i] = InvokeHandlerSafelyAsync(targets[i], @event, ct);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task InvokeHandlerSafelyAsync(Subscription sub, IGatewayEvent @event, CancellationToken ct)
    {
        try
        {
            // SR-M-13: a slow or hung subscriber must not stall the publisher or other subscribers. WaitAsync also
            // bounds handlers that ignore the cancellation token; they keep running detached.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_handlerTimeout);
            await sub.Handler(@event, timeoutCts.Token).WaitAsync(_handlerTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            RecordTimeout(@event);
            _logger.LogWarning(
                "Event handler for {EventType} ({EventId}) in subscription {SubscriptionId} exceeded the {TimeoutMs} ms limit",
                @event.EventType, @event.EventId, sub.Id, (int)_handlerTimeout.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("Event handler for {EventType} cancelled", @event.EventType);
        }
        catch (OperationCanceledException)
        {
            RecordTimeout(@event);
            _logger.LogWarning(
                "Event handler for {EventType} ({EventId}) in subscription {SubscriptionId} timed out",
                @event.EventType, @event.EventId, sub.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Subscriber error handling event {EventType} ({EventId}) in subscription {SubscriptionId}",
                @event.EventType, @event.EventId, sub.Id);
        }
    }

    private static void RecordTimeout(IGatewayEvent @event)
    {
        GatewayDiagnostics.GatewayEventBusHandlerTimeoutCounter.Add(
            1, new KeyValuePair<string, object?>("event_type", @event.EventType));
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
