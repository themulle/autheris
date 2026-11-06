namespace Autheris.Modules.Lakehouse;

using System;
using System.Net.Http;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class LakehouseServiceCollectionExtensions
{
    public static IServiceCollection AddLakehouseIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var gatewayOptions = new GatewayOptions();
        configuration.GetSection(GatewayOptions.SectionName).Bind(gatewayOptions);
        return services.AddLakehouseIntegration(gatewayOptions);
    }

    public static IServiceCollection AddLakehouseIntegration(this IServiceCollection services, GatewayOptions gatewayOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(gatewayOptions);

        services.TryAddTransient<SsrfProtectionHandler>();
        services.AddSingleton<LocalStorageProvider>();

        // SEC E-02 / E-03: Lakehouse uses hardened outbound handler for storage calls.
        services.AddHttpClient(nameof(S3LakehouseStorageProvider))
            .AddSecureOutboundHandlers(EgressIntegrations.Lakehouse);
        services.AddHttpClient(nameof(AzureBlobStorageProvider))
            .AddSecureOutboundHandlers(EgressIntegrations.Lakehouse);

        services.AddSingleton(sp => new S3LakehouseStorageProvider(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<GatewayOptions>>(),
            sp.GetRequiredService<ILogger<S3LakehouseStorageProvider>>()));
        services.AddSingleton(sp => new AzureBlobStorageProvider(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<GatewayOptions>>(),
            sp.GetRequiredService<ILogger<AzureBlobStorageProvider>>()));
        services.AddSingleton<CompositeLakehouseStorageProvider>();

        services.AddSingleton<ILakehouseStorageProvider>(sp => sp.GetRequiredService<CompositeLakehouseStorageProvider>());
        services.AddSingleton<IIcebergMetadataReader, IcebergMetadataReader>();
        services.AddSingleton<IIcebergPartitionPruner, IcebergPartitionPruner>();
        services.AddSingleton<IDeltaMetadataReader, DeltaMetadataReader>();
        services.AddSingleton<IDeltaPartitionPruner, DeltaPartitionPruner>();

        if (!services.Any(d => d.ServiceType == typeof(IDataSourceExecutor) && d.ImplementationType == typeof(LakehouseDataSourceExecutor)))
        {
            services.AddScoped<ILakehouseDataSourceExecutor, LakehouseDataSourceExecutor>();
            services.AddScoped<IDataSourceExecutor, LakehouseDataSourceExecutor>();
            services.AddScoped<IDataSourceExecutor, DeltaLakeDataSourceExecutor>();
            services.AddScoped<IIcebergRestCatalogFederationService, IcebergRestCatalogFederationService>();
        }

        return services;
    }
}
