namespace Autheris.Application.Products;

using System;
using System.Collections.Generic;
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

/// <summary>One tick: re-reads each product this process served on demand (current settings, never a stale copy) and drops the idle results.</summary>
public sealed class ProductIdleDropJob(
    IDataProductRepository products,
    ProductLoadService loader,
    ILogger<ProductIdleDropJob> logger) : IClusterScheduledJob
{
    public async Task ExecuteAsync(ClusterJobContext context, CancellationToken ct)
    {
        var candidates = new List<DataProduct>();
        foreach (var (tenant, schema, name) in loader.TrackedOnDemand())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await products.GetProductAsync(tenant, schema, name, ct).ConfigureAwait(false) is { } product)
                {
                    candidates.Add(product);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Idle drop could not read data product {Product}.", $"{schema}.{name}");
            }
        }

        var dropped = await loader.DropIdleAsync(candidates, ct).ConfigureAwait(false);
        if (dropped > 0)
        {
            logger.LogInformation("Dropped {Count} idle data product results.", dropped);
        }
    }
}
