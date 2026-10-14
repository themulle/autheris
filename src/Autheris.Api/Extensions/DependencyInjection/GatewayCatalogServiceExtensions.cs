namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Interfaces;
using Autheris.Infrastructure.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class GatewayCatalogServiceExtensions
{
    public static IServiceCollection AddAutherisCatalog(this IServiceCollection services)
    {
        services.AddOptions<CatalogDiscoveryOptions>()
            .Configure<Microsoft.Extensions.Configuration.IConfiguration>((o, cfg) => cfg.GetSection(CatalogDiscoveryOptions.SectionName).Bind(o))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Options.IValidateOptions<CatalogDiscoveryOptions>, CatalogDiscoveryOptionsValidator>());
        services.TryAddSingleton<ILocalEmbeddingGenerator, LocalDeterministicEmbeddingGenerator>();
        services.TryAddSingleton<ICatalogSearchEngine, CatalogSearchEngine>();
        services.AddHostedService<CatalogSearchWarmupService>();

        services.TryAddScoped<ICatalogDiscoveryService, CatalogDiscoveryService>();
        services.TryAddScoped<IPrincipalResolverService, PrincipalResolverService>();
        services.TryAddScoped<IDatasourceTestingService, DatasourceTestingService>();
        return services;
    }
}
