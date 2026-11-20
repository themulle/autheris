namespace Autheris.Extensions.DataCatalog;

using Autheris.Application.Catalog.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class DataCatalogServiceCollectionExtensions
{
    /// <summary>
    /// Data catalog governance integration: registers the catalog sync service. The periodic background sync is only
    /// registered when <c>Gateway:Catalog:Enabled</c> is true; the <see cref="IDataCatalogClient"/> implementation is
    /// registered by an optional external module.
    /// </summary>
    public static IServiceCollection AddDataCatalogIntegration(this IServiceCollection services, GatewayOptions gatewayOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(gatewayOptions);

        if (!ExtensionRegistration.TryBegin<DataCatalogIntegrationMarker>(services))
        {
            return services;
        }

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
