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
        services.TryAddSingleton(sp => new ProductLoadService(
            sp.GetRequiredService<IDataProductRepository>(),
            sp.GetRequiredService<IProductQueryRunner>(),
            sp.GetRequiredService<Autheris.Domain.Caching.TableCache.ITableCacheStore>(),
            sp.GetRequiredService<Autheris.Domain.Caching.TableCache.ITableCacheCatalog>(),
            sp.GetRequiredService<Autheris.Application.State.IDistributedClusterStateProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.TableCache.TableCacheOptions>>(),
            sp.GetRequiredService<IAuditWriter>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ILogger<ProductLoadService>>()));
        services.TryAddScoped<ProductScheduleJob>();
        services.AddHostedService<ProductScheduleService>();
        return services;
    }
}
