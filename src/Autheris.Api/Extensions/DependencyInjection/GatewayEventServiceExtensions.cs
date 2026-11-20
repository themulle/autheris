namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.Events;
using Autheris.Application.Security;
using Autheris.Application.Webhooks;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
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

        // Consent event webhooks and the events module share WebhookDeliveryClient + WebhookRetryPolicy; the client keeps the SSRF
        // handler chain and the egress allowlist of the Webhooks integration.
        services.AddSingleton(sp => new WebhookRetryPolicy(
            sp.GetRequiredService<IOptions<GatewayOptions>>().Value.Webhooks,
            sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddHttpClient<WebhookDeliveryClient>()
            .AddSecureOutboundHandlers(EgressIntegrations.Webhooks);

        // The webhook dispatcher, the event bridge and the delivery workers belong to the events module.
        // Configured webhook subscriptions without that module fail startup (see AddGatewayModules).

        // 99 I1-C: governed CDC stream crypto, signal, tail hub, retention and the compatibility feed.
        services.AddAutherisCdcStream(gatewayOptions);

        return services;
    }
}
