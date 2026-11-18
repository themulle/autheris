namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using Autheris.Application.Audit;
using Autheris.Application.Products;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

public static class ProductLoadingServiceExtensions
{
    /// <summary>Stage 1 of data product views. Needs the governed table cache, so it is registered together with it.</summary>
    internal static IServiceCollection AddProductLoading(this IServiceCollection services)
    {
        services.TryAddSingleton<IProductQueryRunner, FederationProductQueryRunner>();
        services.TryAddSingleton<ProductUsageTracker>(sp => new ProductUsageTracker(sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IProductBaseObjectCheck>(sp => new ProductBaseObjectCheck(sp.GetRequiredService<Autheris.Application.Common.Interfaces.ITableMetadataRepository>()));
        services.TryAddSingleton<IProductRefreshTimes>(sp => sp.GetRequiredService<ProductLoadService>());
        services.TryAddSingleton<IProductReadAudit>(sp => new ProductReadAudit(
            sp.GetRequiredService<IDataProductRepository>(),
            sp.GetRequiredService<IProductRefreshTimes>(),
            sp.GetRequiredService<IAuditWriter>(),
            sp.GetService<ProductUsageTracker>(),
            sp.GetService<TimeProvider>()));
        services.TryAddSingleton(sp => new ProductLoadService(
            sp.GetRequiredService<IDataProductRepository>(),
            sp.GetRequiredService<IProductQueryRunner>(),
            sp.GetRequiredService<Autheris.Domain.Caching.TableCache.ITableCacheStore>(),
            sp.GetRequiredService<Autheris.Domain.Caching.TableCache.ITableCacheCatalog>(),
            sp.GetRequiredService<Autheris.Application.State.IDistributedClusterStateProvider>(),
            sp.GetRequiredService<IAuditWriter>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ILogger<ProductLoadService>>(),
            sp.GetService<IProductCatalogStore>(),
            sp.GetService<IProductBaseObjectCheck>(),
            sp.GetService<ProductUsageTracker>()));
        services.TryAddSingleton<IProductResultRouter>(sp => new ProductResultRouter(
            sp.GetRequiredService<IDataProductRepository>(),
            sp.GetRequiredService<ProductLoadService>(),
            sp.GetRequiredService<Autheris.Domain.Caching.TableCache.ITableCacheCatalog>()));
        services.TryAddScoped<ProductScheduleJob>();
        services.AddHostedService<ProductScheduleService>();
        services.TryAddScoped<ProductIdleDropJob>();
        services.AddHostedService<ProductIdleDropService>();
        return services;
    }
}
