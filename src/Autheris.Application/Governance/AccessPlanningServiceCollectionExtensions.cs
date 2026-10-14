namespace Autheris.Application.Governance;

using System;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.State;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

public static class AccessPlanningServiceCollectionExtensions
{
    /// <summary>
    /// Registers the two-phase access planning and administrative control plane services.
    /// </summary>
    public static IServiceCollection AddAutherisGovernancePlans(this IServiceCollection services)
    {
        // ARC-C-05: plans and consumed tokens are shared across replicas only when the cluster state is external.
        // Embedded or in-memory state (single node) keeps them in a bounded process-local store.
        services.AddSingleton<IAccessPlanStore>(sp =>
        {
            var time = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var topology = sp.GetService<IClusterStateTopology>();
            var state = sp.GetService<IDistributedClusterStateProvider>();
            if (topology?.IsClusterWide == true && state != null)
            {
                return new ClusterAccessPlanStore(state, time);
            }

            var options = sp.GetService<IOptions<AccessPlanOptions>>()?.Value ?? new AccessPlanOptions();
            return new InMemoryAccessPlanStore(time, TimeSpan.FromSeconds(options.SweepIntervalSeconds));
        });
        services.AddSingleton<IAccessPlanningService, AccessPlanningService>();
        return services;
    }
}
