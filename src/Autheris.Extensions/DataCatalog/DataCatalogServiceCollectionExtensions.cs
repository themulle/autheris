namespace Autheris.Extensions.DataCatalog;

using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Autheris.Extensions.OpenMetadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class DataCatalogServiceCollectionExtensions
{
    /// <summary>
    /// Multi-catalog governance integration (Microsoft Purview, Collibra, OpenMetadata, Alation): one client per catalog,
    /// the client factory, the catalog sync and the catalog webhook handler. The periodic background sync is only
    /// registered when <c>Gateway:Catalog:Enabled</c> is true.
    /// </summary>
    public static IServiceCollection AddDataCatalogIntegration(this IServiceCollection services, GatewayOptions gatewayOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(gatewayOptions);

        if (!ExtensionRegistration.TryBegin<DataCatalogIntegrationMarker>(services))
        {
            return services;
        }

        services.TryAddTransient<SsrfProtectionHandler>();
        // SEC E-03 / E-04: no redirects (the Alation TOKEN header never reaches a foreign origin), connect-time IP check.
        services.AddHttpClient<PurviewDataCatalogClient>().AddSecureOutboundHandlers(EgressIntegrations.Catalog);
        services.AddHttpClient<CollibraDataCatalogClient>().AddSecureOutboundHandlers(EgressIntegrations.Catalog);
        services.AddHttpClient<AlationCatalogClient>().AddSecureOutboundHandlers(EgressIntegrations.Catalog);

        // OpenMetadata as data catalog reuses the hardened IOpenMetadataClient (bounded reads, secret provider, SSRF handler).
        OpenMetadataServiceCollectionExtensions.AddOpenMetadataHttpClient(services);
        services.TryAddTransient<OpenMetadataCatalogAdapter>();

        services.TryAddSingleton<IDataCatalogClientFactory, DataCatalogClientFactory>();
        services.TryAddScoped<IDataCatalogSyncService, DataCatalogSyncService>();
        services.TryAddScoped<IDataCatalogWebhookHandler, CatalogWebhookHandler>();

        if (gatewayOptions.Catalog.Enabled)
        {
            services.AddHostedService<DataCatalogSyncBackgroundService>();
        }

        return services;
    }

    private sealed class DataCatalogIntegrationMarker
    {
    }
}
