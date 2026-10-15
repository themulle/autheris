namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.State;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Options;
using Autheris.GraphQL.Subscriptions;
using Autheris.Infrastructure.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

/// <summary>
/// WP-ARC-A-13: Registers the CDC event channel (<see cref="RedisCdcEventChannel"/> in cluster-wide mode when
/// <see cref="IConnectionMultiplexer"/> is available, falling back to <see cref="InMemoryCdcEventChannel"/> in single-node mode)
/// and <see cref="CdcSubscriptionGovernor"/>.
/// </summary>
public static class GatewayStreamingServiceExtensions
{
    public static IServiceCollection AddAutherisStreaming(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryCdcEventChannel>();
        services.AddSingleton<ICdcEventChannel>(sp =>
        {
            var localChannel = sp.GetRequiredService<InMemoryCdcEventChannel>();
            var topology = sp.GetService<IClusterStateTopology>();
            var multiplexer = sp.GetService<IConnectionMultiplexer>();

            if (topology?.IsClusterWide == true && multiplexer is not null)
            {
                return new RedisCdcEventChannel(
                    multiplexer,
                    localChannel,
                    sp.GetRequiredService<ILogger<RedisCdcEventChannel>>());
            }

            return localChannel;
        });

        services.AddSingleton<CdcSubscriptionGovernor>(sp => new CdcSubscriptionGovernor(
            sp.GetRequiredService<IOptions<GatewayOptions>>(),
            sp.GetRequiredService<ILogger<CdcSubscriptionGovernor>>(),
            sp.GetService<IDistributedClusterStateProvider>(),
            sp.GetService<IClusterStateTopology>()));

        return services;
    }
}
