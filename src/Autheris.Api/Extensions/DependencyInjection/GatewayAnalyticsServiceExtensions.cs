namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.Analytics;
using Autheris.Application.Data;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

/// <summary>
/// Registers governed analytical aggregation (#28, #37) and dbt Semantic Layer metric execution services (#38).
/// </summary>
public static class GatewayAnalyticsServiceExtensions
{
    public static IServiceCollection AddGatewayAnalyticsServices(this IServiceCollection services)
    {
        services.AddOptions<AnalyticsOptions>()
            .Configure<IConfiguration>((o, cfg) => cfg.GetSection(AnalyticsOptions.SectionName).Bind(o))
            .ValidateOnStart();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AnalyticsOptions>, AnalyticsOptionsValidator>());
        services.TryAddSingleton<IGovernedTableReadPlanner, GovernedTableReadPlanner>();
        services.TryAddSingleton<ISemanticCatalogService, InMemorySemanticCatalog>();
        services.TryAddScoped<IDbtSemanticManifestIngestionService, DbtSemanticManifestIngestionService>();
        services.TryAddScoped<IGovernedAggregationService, GovernedAggregationService>();
        services.TryAddScoped<ISemanticMetricQueryService, SemanticMetricQueryService>();
        return services;
    }
}
