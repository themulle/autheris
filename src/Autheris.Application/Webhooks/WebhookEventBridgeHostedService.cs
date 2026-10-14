namespace Autheris.Application.Webhooks;

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autheris.Application.Events;
using Autheris.Domain.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Hosted service that bridges the in-process event bus to external webhook dispatching.
/// Events are queued in a bounded channel and delivered by background workers so that publishing
/// never waits for outbound HTTP calls or retries (SR-M-13). When the queue is full the event is dropped and logged.
/// </summary>
public sealed class WebhookEventBridgeHostedService : IHostedService, IDisposable
{
    /// <summary>Default capacity of the delivery queue.</summary>
    public const int DefaultQueueCapacity = 1024;

    private const int WorkerCount = 4;

    private readonly IGatewayEventBus _eventBus;
    private readonly IWebhookDispatcherService _webhookDispatcher;
    private readonly ILogger<WebhookEventBridgeHostedService> _logger;
    private readonly Channel<IGatewayEvent> _queue;
    private IDisposable? _subscription;
    private CancellationTokenSource? _workersCts;
    private Task[] _workers = [];

    public WebhookEventBridgeHostedService(
        IGatewayEventBus eventBus,
        IWebhookDispatcherService webhookDispatcher,
        ILogger<WebhookEventBridgeHostedService> logger,
        int queueCapacity = DefaultQueueCapacity)
    {
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _webhookDispatcher = webhookDispatcher ?? throw new ArgumentNullException(nameof(webhookDispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _queue = Channel.CreateBounded<IGatewayEvent>(new BoundedChannelOptions(Math.Max(1, queueCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting WebhookEventBridgeHostedService and subscribing to IGatewayEventBus");

        _workersCts = new CancellationTokenSource();
        var token = _workersCts.Token;
        _workers = new Task[WorkerCount];
        for (var i = 0; i < _workers.Length; i++)
        {
            _workers[i] = Task.Run(() => RunWorkerAsync(token), CancellationToken.None);
        }

        _subscription = _eventBus.Subscribe<IGatewayEvent>((@event, _) =>
        {
            if (!_queue.Writer.TryWrite(@event))
            {
                _logger.LogWarning(
                    "Webhook delivery queue is full; dropping event {EventType} ({EventId})",
                    @event.EventType, @event.EventId);
            }

            return Task.CompletedTask;
        });

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;

        _queue.Writer.TryComplete();
        var workersCts = _workersCts;
        if (workersCts != null)
        {
            // Give in-flight deliveries until the host shutdown token fires, then cancel the rest.
            using var registration = cancellationToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), workersCts);
            try
            {
                await Task.WhenAll(_workers).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown requested while draining.
            }

            workersCts.Dispose();
            _workersCts = null;
        }

        _workers = [];
    }

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var @event in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _webhookDispatcher.DispatchEventAsync(@event, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error in WebhookEventBridge for event {EventType} ({EventId})",
                        @event.EventType, @event.EventId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _workersCts?.Dispose();
        _workersCts = null;
    }
}
