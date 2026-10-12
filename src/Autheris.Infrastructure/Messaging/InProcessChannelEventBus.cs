using System.Collections.Concurrent;
using System.Threading.Channels;
using Autheris.Domain.Diagnostics;

namespace Autheris.Infrastructure.Messaging;

public sealed class InProcessChannelEventBus : IEventBus, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, List<DelegateHandler>> _subscribers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<EventEnvelope> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumerTask;

    public event Action? ConnectionRestored;
    public void SimulateConnectionRestored() => ConnectionRestored?.Invoke();

    private sealed record EventEnvelope(string Channel, object Message);
    private sealed record DelegateHandler(Func<object, Task> Handler);

    public InProcessChannelEventBus(int capacity = 10_000)
    {
        var boundedOptions = new BoundedChannelOptions(Math.Max(1, capacity))
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        };
        _channel = Channel.CreateBounded<EventEnvelope>(boundedOptions);
        _consumerTask = Task.Run(ProcessEventsAsync);
    }

    private static bool IsInvalidationChannel(string channel)
    {
        if (string.IsNullOrEmpty(channel)) return false;
        return channel.Contains("invalidate", StringComparison.OrdinalIgnoreCase)
            || channel.Contains("epoch", StringComparison.OrdinalIgnoreCase);
    }

    public async Task PublishAsync<T>(string channel, T message, CancellationToken ct = default)
    {
        if (message == null) return;

        // AR-03: Invalidation channels are dispatched directly to subscribers without dropping or channel saturation delays
        if (IsInvalidationChannel(channel))
        {
            await DispatchInvalidationAsync(channel, message).ConfigureAwait(false);
            return;
        }

        // Standard/telemetry channels use bounded queue with drop metrics
        if (!_channel.Writer.TryWrite(new EventEnvelope(channel, message)))
        {
            Interlocked.Increment(ref _droppedCount);
            GatewayDiagnostics.EventBusDroppedCounter.Add(1, KeyValuePair.Create<string, object?>("channel", channel));
        }
    }

    private long _droppedCount;
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    private async Task DispatchInvalidationAsync(string channel, object message)
    {
        if (_subscribers.TryGetValue(channel, out var list))
        {
            DelegateHandler[] targets;
            lock (list)
            {
                targets = list.ToArray();
            }

            foreach (var target in targets)
            {
                try
                {
                    await target.Handler(message).ConfigureAwait(false);
                }
                catch
                {
                    // Invalidation handlers should not crash publisher
                }
            }
        }
    }

    public Task<long> IncrementCounterAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var val = _counters.AddOrUpdate(key, 1, (_, current) => current + 1);
        return Task.FromResult(val);
    }

    public Task<long> GetCounterAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var val = _counters.TryGetValue(key, out var current) ? current : 0L;
        return Task.FromResult(val);
    }

    public IDisposable Subscribe<T>(string channel, Func<T, Task> handler)
    {
        var list = _subscribers.GetOrAdd(channel, _ => new List<DelegateHandler>());
        var wrapped = new DelegateHandler(obj =>
        {
            if (obj is T typed)
            {
                return handler(typed);
            }
            return Task.CompletedTask;
        });

        lock (list)
        {
            list.Add(wrapped);
        }

        return new Unsubscriber(() =>
        {
            lock (list)
            {
                list.Remove(wrapped);
            }
        });
    }

    private async Task ProcessEventsAsync()
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token))
            {
                while (reader.TryRead(out var envelope))
                {
                    if (_subscribers.TryGetValue(envelope.Channel, out var list))
                    {
                        DelegateHandler[] targets;
                        lock (list)
                        {
                            targets = list.ToArray();
                        }

                        foreach (var target in targets)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var handlerTask = target.Handler(envelope.Message);
                                    _ = handlerTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                                    await handlerTask.WaitAsync(TimeSpan.FromSeconds(2), _cts.Token);
                                }
                                catch
                                {
                                    // Log or swallow in event loop to keep processor alive
                                }
                            }, _cts.Token);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _channel.Writer.Complete();
        try
        {
            await _consumerTask;
        }
        catch
        {
            // Ignore
        }
        _cts.Dispose();
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly Action _unsubscribe;
        private bool _disposed;

        public Unsubscriber(Action unsubscribe) => _unsubscribe = unsubscribe;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _unsubscribe();
            }
        }
    }
}
