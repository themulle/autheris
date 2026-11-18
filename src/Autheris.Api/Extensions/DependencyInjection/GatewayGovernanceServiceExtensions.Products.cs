namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Products;
using Autheris.Application.Products.Sync;
using Autheris.Application.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public static partial class GatewayGovernanceServiceExtensions
{
    /// <summary>Data products: creation authority, product-owner rules, base-owner notification and column classification.</summary>
    private static void AddDataProducts(IServiceCollection services)
    {
        services.AddSingleton<IProductCatalogColumns>(sp => new ProductCatalogColumns(sp.GetRequiredService<ITableMetadataRepository>()));
        // the owner link and the catalog object are persisted by the governance store; a host without it keeps products unpersisted (fail closed)
        services.AddSingleton<IProductCatalogOwnerStore>(sp => new Autheris.Infrastructure.Persistence.Products.ProductCatalogOwnerStore(sp.GetRequiredService<Autheris.Infrastructure.Persistence.GovernanceRepository>()));
        services.AddSingleton<IOnDemandProductStore>(sp => new Autheris.Infrastructure.Persistence.Products.OnDemandProductStore(sp.GetRequiredService<Autheris.Infrastructure.Persistence.GovernanceRepository>()));
        services.AddSingleton<IProductCatalogStore>(sp => new ProductCatalogStore(
            sp.GetRequiredService<ITableMetadataRepository>(),
            sp.GetRequiredService<IClassificationRepository>(),
            sp.GetService<TimeProvider>()));
        services.AddScoped<IProductReadAccess, ProductReadAccess>();
        // refresh times come from the stage 1 loader when the table cache is enabled; without it no product has a result
        services.AddScoped(sp => new ProductDiscoveryService(
            sp.GetRequiredService<IDataProductRepository>(),
            sp.GetRequiredService<IProductReadAccess>(),
            sp.GetService<IProductRefreshTimes>() ?? NoProductRefreshTimes.Instance,
            sp.GetService<TimeProvider>(),
            sp.GetService<ProductUsageTracker>()));
        services.AddSingleton(sp => new ProductAuthorityService(
            sp.GetRequiredService<IDataOwnershipRepository>(),
            sp.GetRequiredService<IDataProductRepository>(),
            sp.GetService<TimeProvider>()));
        services.AddSingleton(sp => new DataProductService(
            sp.GetRequiredService<IDataProductRepository>(),
            sp.GetRequiredService<ProductAuthorityService>(),
            sp.GetRequiredService<IProductCatalogColumns>(),
            sp.GetRequiredService<TrinoSqlEngine.ISqlEngine>(),
            sp.GetRequiredService<IDataOwnershipRepository>(),
            sp.GetRequiredService<IAuditWriter>(),
            sp.GetService<IConsentEventOutbox>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ILogger<DataProductService>>(),
            sp.GetService<IProductCatalogOwnerStore>() is { } ownerStore ? new ProductOwnershipSync(ownerStore) : null,
            sp.GetService<IProductCatalogStore>()));
        services.AddSingleton(sp => new ProductSyncService(
            sp.GetRequiredService<DataProductService>(),
            sp.GetRequiredService<IDataProductRepository>(),
            sp.GetRequiredService<IAuditWriter>(),
            sp.GetService<IProcedureRegistry>(),
            sp.GetService<TimeProvider>()));
        services.AddSingleton(sp => new ProductSyncWebhookService(
            sp.GetService<IKeyVaultSecretProvider>(),
            sp.GetService<IDistributedClusterStateProvider>(),
            sp.GetRequiredService<IAuditWriter>(),
            sp.GetService<TimeProvider>()));
        services.AddSingleton(sp => new DataSourceOwnerService(
            sp.GetRequiredService<IDataProductRepository>(),
            sp.GetRequiredService<IAuditWriter>(),
            sp.GetService<TimeProvider>()));
    }
}
