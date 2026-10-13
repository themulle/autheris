namespace Autheris.Application.Webhooks;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events;
using Autheris.Domain.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Hosted service that bridges the in-process event bus to external webhook dispatching.
/// </summary>
public sealed class WebhookEventBridgeHostedService : IHostedService
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in WebhookEventBridge for event {EventType} ({EventId})",
                    @event.EventType, @event.EventId);
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
}
