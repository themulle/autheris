namespace Autheris.Application.Webhooks;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Events;

/// <summary>
/// Service responsible for dispatching CloudEvents to registered external webhook endpoints.
/// </summary>
public interface IWebhookDispatcherService
{
    /// <summary>
    /// Dispatches a domain event to all matching webhook subscriptions asynchronously.
    /// </summary>
    Task DispatchEventAsync(IGatewayEvent @event, CancellationToken ct = default);
}
