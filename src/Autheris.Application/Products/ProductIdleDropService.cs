namespace Autheris.Application.Products;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Runtime.Hosting;
using Autheris.Application.State;
using Autheris.Domain.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>Cluster-singleton ticker that drops the cached result of <c>onDemand</c> products nobody read for their idle period, lease <c>products:idle-drop</c>.</summary>
public class ProductIdleDropService(
    IServiceScopeFactory scopeFactory,
    IDistributedClusterStateProvider clusterState,
    ILogger<ProductIdleDropService> logger,
    TimeProvider? timeProvider = null)
    : ClusterScheduledService<ProductIdleDropJob>(scopeFactory, clusterState, logger, timeProvider)
{
    protected override string LeaseKey => "products:idle-drop";

    protected override ClusterSchedule GetSchedule() => new(
        Enabled: true,
        Interval: TimeSpan.FromMinutes(1),
        InitialDelay: TimeSpan.FromSeconds(45),
        LeaseTtl: TimeSpan.FromMinutes(5),
        MaxJitter: TimeSpan.FromSeconds(5));
}

/// <summary>One tick: pages the <c>onDemand</c> products of every tenant (current settings, never a stale copy) and drops the idle results of this node.</summary>
public sealed class ProductIdleDropJob(
    IOnDemandProductStore products,
    ProductLoadService loader,
    ILogger<ProductIdleDropJob> logger) : IClusterScheduledJob
{
    private const int PageSize = 100;

    public async Task ExecuteAsync(ClusterJobContext context, CancellationToken ct)
    {
        var dropped = 0;
        DataProduct? after = null;
        while (true)
        {
            var page = await products.ListOnDemandProductsAsync(after, PageSize, ct).ConfigureAwait(false);
            await loader.ReprojectAgedAsync(page, ct).ConfigureAwait(false);
            dropped += await loader.DropIdleAsync(page, ct).ConfigureAwait(false);
            if (page.Count < PageSize)
            {
                break;
            }

            after = page[^1];
        }

        if (dropped > 0)
        {
            logger.LogInformation("Dropped {Count} idle data product results.", dropped);
        }
    }
}
