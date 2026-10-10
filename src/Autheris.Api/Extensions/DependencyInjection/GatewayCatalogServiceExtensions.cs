namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class GatewayCatalogServiceExtensions
{
    public static IServiceCollection AddAutherisCatalog(this IServiceCollection services)
    {
        services.TryAddScoped<ICatalogDiscoveryService, CatalogDiscoveryService>();
        services.TryAddScoped<IPrincipalResolverService, PrincipalResolverService>();
        services.TryAddScoped<IDatasourceTestingService, DatasourceTestingService>();
        return services;
    }
}
