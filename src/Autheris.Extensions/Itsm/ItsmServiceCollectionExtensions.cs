namespace Autheris.Extensions.Itsm;

using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class ItsmServiceCollectionExtensions
{
    /// <summary>
    /// ITSM integration: outbound REST clients (ServiceNow Table API, Jira Cloud REST v3) and the inbound
    /// ITSM webhook handler (HMAC per instance, replay cache). Always registered, because the core
    /// <c>ItsmWorkflowDispatcher</c> and the webhook endpoints resolve them; the outbox/recertification
    /// workers stay in the core and are gated by <c>Gateway:Itsm:Enabled</c>.
    /// </summary>
    public static IServiceCollection AddItsmIntegration(this IServiceCollection services, GatewayOptions gatewayOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(gatewayOptions);

        if (!ExtensionRegistration.TryBegin<ItsmIntegrationMarker>(services))
        {
            return services;
        }

        services.TryAddTransient<SsrfProtectionHandler>();
        // SEC E-03: hardened primary handler (no redirects, connect-time IP check) + SSRF handler with the ITSM allowlist.
        services.AddHttpClient<ServiceNowTableApiClient>().AddSecureOutboundHandlers(EgressIntegrations.Itsm);
        services.AddHttpClient<JiraCloudRestClient>().AddSecureOutboundHandlers(EgressIntegrations.Itsm);

        // SEC EX-12: resolve the workflow clients through their typed HttpClient registration (with SSRF handler).
        // A type-based ServiceDescriptor.Scoped<IItsmWorkflowClient, X>() would construct X with the default HttpClient
        // (without handler). TryAddEnumerable cannot be used for factory descriptors (implementation type not
        // distinguishable), duplicates are prevented by the integration marker above.
        services.AddScoped<IItsmWorkflowClient>(sp => sp.GetRequiredService<ServiceNowTableApiClient>());
        services.AddScoped<IItsmWorkflowClient>(sp => sp.GetRequiredService<JiraCloudRestClient>());

        services.TryAddScoped<IItsmWebhookHandler, ItsmWebhookHandler>();

        return services;
    }

    private sealed class ItsmIntegrationMarker
    {
    }
}
