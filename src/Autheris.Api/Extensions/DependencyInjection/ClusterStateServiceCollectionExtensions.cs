namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.State;
using Autheris.Infrastructure.State;
using Microsoft.Extensions.DependencyInjection;

public static class ClusterStateServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IDistributedClusterStateProvider"/> matching <paramref name="mode"/> together with the
    /// <see cref="IClusterStateTopology"/> describing it. <see cref="ClusterStateMode.Embedded"/> and
    /// <see cref="ClusterStateMode.External"/> use the Redis provider (the caller registers the connection multiplexer);
    /// <see cref="ClusterStateMode.InMemory"/> uses the process-local provider.
    /// </summary>
    public static IServiceCollection AddClusterState(this IServiceCollection services, ClusterStateMode mode)
    {
        if (mode == ClusterStateMode.InMemory)
        {
            services.AddSingleton<IDistributedClusterStateProvider, InMemoryClusterStateProvider>();
        }
        else
        {
            services.AddSingleton<IDistributedClusterStateProvider, RedisClusterStateProvider>();
        }

        services.AddSingleton<IClusterStateTopology>(new ClusterStateTopology(mode));
        return services;
    }
}
