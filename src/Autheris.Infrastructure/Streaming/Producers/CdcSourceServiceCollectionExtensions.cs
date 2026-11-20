namespace Autheris.Infrastructure.Streaming.Producers;

using System.Linq;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class CdcSourceServiceCollectionExtensions
{
    /// <summary>
    /// CDC sources: the native MSSQL Change Tracking producer (F-CDC-02), which appends to the durable governed stream
    /// (<c>ICdcStreamStore</c>, provided by the host), and the Debezium envelope parser (<see cref="Autheris.Application.Streaming.DebeziumCdcParser"/>, used statically
    /// by the CDC endpoint). The producer (<see cref="CdcProducerService"/>, one fenced lease per tracked table) is only registered when
    /// <c>Gateway:MssqlChangeTracking:Enabled</c> is true. The host also provides <c>ICdcStreamCrypto</c>, <c>ICdcPayloadCodec</c> and
    /// <c>ICdcStreamSignal</c>.
    /// </summary>
    public static IServiceCollection AddCdcSourceIntegration(this IServiceCollection services, GatewayOptions gatewayOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(gatewayOptions);

        if (services.Any(d => d.ServiceType == typeof(CdcSourceIntegrationMarker)))
        {
            return services;
        }

        services.AddSingleton(new CdcSourceIntegrationMarker());

        services.TryAddSingleton<ICdcTableActivator, CdcTableActivator>();
        services.TryAddSingleton<CdcChangeEventBuilder>();
        services.TryAddSingleton<IMssqlChangeTrackingPoller, MssqlChangeTrackingPoller>();
        if (gatewayOptions.MssqlChangeTracking.Enabled)
        {
            services.TryAddSingleton<CdcPollFailureTracker>();
            services.AddHostedService<CdcProducerService>();
        }

        return services;
    }

    private sealed class CdcSourceIntegrationMarker
    {
    }
}
