namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.Events;
using Autheris.Application.Security;
using Autheris.Application.Webhooks;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;

public static class GatewayEventServiceExtensions
{
    public static IServiceCollection AddAutherisEvents(
        this IServiceCollection services,
        GatewayOptions gatewayOptions)
    {
        services.AddSingleton<IGatewayEventBus, InMemoryGatewayEventBus>();

        services.AddHttpClient<IWebhookDispatcherService, WebhookDispatcherService>()
            .AddSecureOutboundHandlers(EgressIntegrations.Webhooks);

        services.AddHostedService<WebhookEventBridgeHostedService>();

        return services;
    }
}
