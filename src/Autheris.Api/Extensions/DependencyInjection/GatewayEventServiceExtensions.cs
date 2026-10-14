namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.Events;
using Autheris.Application.Security;
using Autheris.Application.Webhooks;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class GatewayEventServiceExtensions
{
    public static IServiceCollection AddAutherisEvents(
        this IServiceCollection services,
        GatewayOptions gatewayOptions)
    {
        services.AddOptions<EventBusOptions>()
            .BindConfiguration(EventBusOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<EventBusOptions>, EventBusOptionsValidator>();

        services.AddSingleton(sp => new InMemoryGatewayEventBus(
            sp.GetRequiredService<ILogger<InMemoryGatewayEventBus>>(),
            sp.GetRequiredService<IOptions<EventBusOptions>>().Value));
        services.AddSingleton<IGatewayEventBus>(sp => sp.GetRequiredService<InMemoryGatewayEventBus>());
        services.AddSingleton<IGatewayEventBusDrain>(sp => sp.GetRequiredService<InMemoryGatewayEventBus>());

        if (gatewayOptions.Webhooks.Enabled)
        {
            // WP-ARC-A-02: durable webhook outbox. The bridge enqueues; the delivery service (any node) drains with leases.
            services.AddSingleton<IWebhookDispatcherService, WebhookDispatcherService>();
            services.AddSingleton(sp => new WebhookRetryPolicy(
                sp.GetRequiredService<IOptions<GatewayOptions>>().Value.Webhooks,
                sp.GetService<TimeProvider>() ?? TimeProvider.System));
            services.AddHttpClient<WebhookDeliveryClient>()
                .AddSecureOutboundHandlers(EgressIntegrations.Webhooks);
            services.AddSingleton<WebhookSubscriptionResolver>();
            services.AddHostedService<WebhookEventBridgeHostedService>();
            services.AddHostedService<WebhookDeliveryHostedService>();
        }

        return services;
    }
}
