namespace Autheris.Extensions.DataCatalog;

using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Autheris.Extensions.OpenMetadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class DataCatalogServiceCollectionExtensions
{
    /// <summary>
    /// OpenMetadata-backed data catalog governance integration: registers the OpenMetadata catalog adapter and the
    /// catalog sync service. The periodic background sync is only registered when <c>Gateway:Catalog:Enabled</c> is true.
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

        // OpenMetadata as data catalog reuses the hardened IOpenMetadataClient (bounded reads, secret provider, SSRF handler).
        OpenMetadataServiceCollectionExtensions.AddOpenMetadataHttpClient(services);
        services.TryAddTransient<OpenMetadataCatalogAdapter>();
        services.TryAddTransient<IDataCatalogClient, OpenMetadataCatalogAdapter>();

        services.TryAddScoped<IDataCatalogSyncService, DataCatalogSyncService>();

        if (gatewayOptions.Catalog.Enabled)
        {
            services.TryAddScoped<DataCatalogSyncJob>();
            services.AddHostedService<DataCatalogSyncBackgroundService>();
        }

        return services;
    }

    private sealed class DataCatalogIntegrationMarker
    {
    }
}
