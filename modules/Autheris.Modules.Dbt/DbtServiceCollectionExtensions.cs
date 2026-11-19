namespace Autheris.Modules.Dbt;

using System;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Governance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class DbtServiceCollectionExtensions
{
    /// <summary>dbt (data build tool) integration (F-DATA-11): manifest ingestion, exposures, contracts and the verified webhook handler.</summary>
    public static IServiceCollection AddDbtIntegration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ITelemetryMetricsProvider, InMemoryTelemetryMetricsProvider>();
        services.TryAddSingleton<IDbtTestTargetMap, DbtTestTargetMap>();
        // Core consumers of the verifier exist only through dbt; a missing verifier would silently accept owners (NotConfigured).
        services.TryAddSingleton<IDataOwnerIdentityVerifier, ScimDataOwnerIdentityVerifier>();
        services.Replace(ServiceDescriptor.Singleton<IDbtHealthCircuitBreaker, DbtHealthCircuitBreaker>());
        services.AddSemanticCatalog();
        services.TryAddScoped<IDbtSemanticManifestIngestionService, DbtSemanticManifestIngestionService>();
        services.TryAddScoped<IDbtMetadataIngestionService, DbtMetadataIngestionService>();
        services.TryAddScoped<IDbtExposurePublisher, DbtExposurePublisher>();
        services.TryAddScoped<IDbtContractValidator, DbtContractValidator>();
        services.Replace(ServiceDescriptor.Scoped<IDbtCloudWebhookHandler, DbtWebhookReceiver>());

        return services;
    }
}
