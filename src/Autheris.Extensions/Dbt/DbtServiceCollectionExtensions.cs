namespace Autheris.Extensions.Dbt;

using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class DbtServiceCollectionExtensions
{
    /// <summary>dbt (data build tool) integration (F-DATA-11): manifest ingestion, exposures, contracts and webhooks.</summary>
    public static IServiceCollection AddDbtIntegration(this IServiceCollection services, GatewayOptions gatewayOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(gatewayOptions);

        if (!ExtensionRegistration.TryBegin<DbtIntegrationMarker>(services))
        {
            return services;
        }

        services.TryAddSingleton<ITelemetryMetricsProvider, InMemoryTelemetryMetricsProvider>();
        services.TryAddScoped<IDbtMetadataIngestionService, DbtMetadataIngestionService>();
        services.TryAddScoped<IDbtExposurePublisher, DbtExposurePublisher>();
        services.TryAddScoped<IDbtContractValidator, DbtContractValidator>();
        services.TryAddScoped<IDbtWebhookReceiver, DbtWebhookReceiver>();

        return services;
    }

    private sealed class DbtIntegrationMarker
    {
    }
}
