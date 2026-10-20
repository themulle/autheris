namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using Autheris.Application.Caching.TableCache;
using Autheris.Application.Caching.TableCache.Engines;
using Autheris.Application.Caching.TableCache.Governance;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Options.TableCache;
using Autheris.Infrastructure.Caching.TableCache;
using Autheris.Api.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class TableCacheServiceCollectionExtensions
{
    public static IServiceCollection AddGovernedTableCache(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(TableCacheOptions.SectionName);
        services.Configure<TableCacheOptions>(section);

        services.TryAddSingleton<ITableCacheCatalog, TableCacheCatalog>();

        // Register storage engine based on configuration
        var tableCacheOptions = section.Get<TableCacheOptions>() ?? new TableCacheOptions();
        if (tableCacheOptions.DefaultStorageTier == TableCacheStorageTier.Memory)
        {
            services.TryAddSingleton<ITableCacheStore, InMemoryTableCacheStore>();
        }
        else
        {
            services.TryAddSingleton<ITableCacheStore, DuckDbPersistentTableCacheStore>();
        }

        services.TryAddSingleton<ITableCacheManager, TableCacheManager>();
        services.TryAddSingleton<ITableCacheQueryRouter, TableCacheQueryRouter>();
        services.TryAddSingleton<ITableCacheInvalidator, TableCacheInvalidator>();

        services.TryAddSingleton<PeriodicTableCacheRefresher>();
        services.TryAddSingleton<TtlTableCacheInvalidator>();
        services.TryAddSingleton<CdcTableCacheConsumer>();
        services.TryAddSingleton<GovernedTableCacheExecutionService>();

        services.AddHostedService<TableCacheBackgroundWorker>();

        return services;
    }
}
