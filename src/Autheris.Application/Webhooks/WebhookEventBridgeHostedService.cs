namespace Autheris.Application.Webhooks;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events;
using Autheris.Domain.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Hosted service that bridges the in-process event bus to the durable webhook outbox. The bus already queues per subscriber
/// (drop-oldest) and enforces a handler timeout (SR-M-13); the handler only awaits the enqueue, never an outbound HTTP call.
/// </summary>
public sealed class WebhookEventBridgeHostedService : IHostedService, IDisposable
{
    private readonly IGatewayEventBus _eventBus;
    private readonly IWebhookDispatcherService _webhookDispatcher;
    private readonly ILogger<WebhookEventBridgeHostedService> _logger;
    private IDisposable? _subscription;

    public WebhookEventBridgeHostedService(
        IGatewayEventBus eventBus,
        IWebhookDispatcherService webhookDispatcher,
        ILogger<WebhookEventBridgeHostedService> logger)
    {
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _webhookDispatcher = webhookDispatcher ?? throw new ArgumentNullException(nameof(webhookDispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting WebhookEventBridgeHostedService and subscribing to IGatewayEventBus");

        _subscription = _eventBus.Subscribe<IGatewayEvent>(async (@event, ct) =>
        {
            try
            {
                await _webhookDispatcher.DispatchEventAsync(@event, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Bus timeout or shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Unexpected error in WebhookEventBridge for event {EventType} ({EventId}) ({ErrorType})",
                    @event.EventType, @event.EventId, ex.GetType().Name);
            }
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
