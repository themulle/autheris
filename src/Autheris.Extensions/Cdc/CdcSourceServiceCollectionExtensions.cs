namespace Autheris.Extensions.Cdc;

using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class CdcSourceServiceCollectionExtensions
{
    /// <summary>
    /// CDC sources feeding the core stream backbone (ICdcEventChannel): native MSSQL Change Tracking poller
    /// (F-CDC-02) and the Debezium envelope parser (<see cref="DebeziumCdcParser"/>, used statically by the CDC endpoint).
    /// The polling worker is only registered when <c>Gateway:MssqlChangeTracking:Enabled</c> is true.
    /// </summary>
    public static IServiceCollection AddCdcSourceIntegration(this IServiceCollection services, GatewayOptions gatewayOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(gatewayOptions);

        if (!ExtensionRegistration.TryBegin<CdcSourceIntegrationMarker>(services))
        {
            return services;
        }

        services.TryAddSingleton<IMssqlWatermarkStore, InMemoryMssqlWatermarkStore>();
        services.TryAddSingleton<IMssqlChangeTrackingPoller, MssqlChangeTrackingPoller>();
        if (gatewayOptions.MssqlChangeTracking.Enabled)
        {
            services.AddHostedService<MssqlChangeTrackingHostedService>();
        }

        return services;
    }

    private sealed class CdcSourceIntegrationMarker
    {
    }
}
