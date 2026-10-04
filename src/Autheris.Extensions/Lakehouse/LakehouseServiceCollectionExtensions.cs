namespace Autheris.Extensions.Lakehouse;

using System.Net.Http;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class LakehouseServiceCollectionExtensions
{
    /// <summary>
    /// Apache Iceberg lakehouse connector (P4 / ADR-015): storage providers (local, S3, Azure Blob – the HTTP based ones
    /// with SSRF handler), metadata reader, partition pruner and the lakehouse <see cref="IDataSourceExecutor"/>.
    /// Always registered (the executor only handles tables routed to the lakehouse data source type).
    /// </summary>
    public static IServiceCollection AddLakehouseIntegration(this IServiceCollection services, GatewayOptions gatewayOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(gatewayOptions);

        if (!ExtensionRegistration.TryBegin<LakehouseIntegrationMarker>(services))
        {
            return services;
        }

        services.TryAddTransient<SsrfProtectionHandler>();
        services.AddSingleton<LocalStorageProvider>();
        // SEC E-02 / E-03: Lakehouse never uses the egress allowlist (target URLs come from Iceberg manifests).
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
        services.AddScoped<ILakehouseDataSourceExecutor, LakehouseDataSourceExecutor>();
        services.AddScoped<IDataSourceExecutor, LakehouseDataSourceExecutor>();

        return services;
    }

    private sealed class LakehouseIntegrationMarker
    {
    }
}
