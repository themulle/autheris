namespace Autheris.Modules.Events;

using System;
using Autheris.Application.Events.Services;
using Autheris.Application.Streaming.Services;
using Autheris.Application.Webhooks;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>The CDC webhook feature flag as read from the module configuration while the services were being registered.</summary>
public sealed record CdcWebhooksFeatureFlag(bool Enabled);

public static class EventsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the module services. Only delivery surfaces are registered here: the governed stores, the stream crypto,
    /// the stream enforcer, the webhook outbox and the secret protector are host services and are never registered by the module.
    /// The hosted services read their feature options at run time and stay idle while their feature is disabled.
    /// </summary>
    public static IServiceCollection AddEventsModule(this IServiceCollection services, IConfiguration moduleConfiguration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(moduleConfiguration);

        var cdcSection = moduleConfiguration.GetSection(CdcWebhooksOptions.SectionName);
        var cdcWebhooksEnabled = cdcSection.GetValue(nameof(CdcWebhooksOptions.Enabled), new CdcWebhooksOptions().Enabled);

        services.AddSingleton<IValidateOptions<CdcWebhooksOptions>, CdcWebhooksOptionsValidator>();
        services.AddOptions<CdcWebhooksOptions>()
            .Bind(cdcSection)
            .ValidateOnStart();
        services.AddSingleton(new CdcWebhooksFeatureFlag(cdcWebhooksEnabled));

        // Configured event webhooks (Gateway:Webhooks stays a host section): the bridge and the dispatcher idle while it is disabled.
        services.AddSingleton<WebhookSubscriptionResolver>();
        services.AddHostedService<WebhookDeliveryHostedService>();
        services.AddSingleton<IWebhookDispatcherService, WebhookDispatcherService>();
        services.AddHostedService<WebhookEventBridgeHostedService>();

        services.AddSingleton<CdcStreamAdminService>();

        // While disabled no delivery service runs and the subscription endpoints answer 501.
        if (cdcWebhooksEnabled)
        {
            services.AddHostedService<CdcWebhookDeliveryService>();
            services.AddHostedService<Autheris.Api.Hosting.CdcWebhookStartupCheck>();
        }

        return services;
    }
}
