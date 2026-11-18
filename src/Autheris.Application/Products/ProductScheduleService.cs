namespace Autheris.Application.Products;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Runtime.Hosting;
using Autheris.Application.State;
using Autheris.Domain.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Cluster-singleton ticker of the <c>schedule</c> load mode (fixed interval, no cron expressions): once a minute it loads every scheduled
/// view whose last load is older than its interval, lease <c>products:schedule</c>.
/// </summary>
public class ProductScheduleService(
    IServiceScopeFactory scopeFactory,
    IDistributedClusterStateProvider clusterState,
    ILogger<ProductScheduleService> logger,
    TimeProvider? timeProvider = null)
    : ClusterScheduledService<ProductScheduleJob>(scopeFactory, clusterState, logger, timeProvider)
{
    protected override string LeaseKey => "products:schedule";

    protected override ClusterSchedule GetSchedule() => new(
        Enabled: true,
        Interval: LoadSettings.MinInterval,
        InitialDelay: TimeSpan.FromSeconds(30),
        LeaseTtl: TimeSpan.FromMinutes(5),
        MaxJitter: TimeSpan.FromSeconds(5));
}

/// <summary>One tick: pages through the scheduled views of all tenants and loads the due ones. A failing product never blocks the others.</summary>
public sealed class ProductScheduleJob(
    IDataProductRepository products,
    ProductLoadService loader,
    ILogger<ProductScheduleJob> logger,
    TimeProvider? timeProvider = null) : IClusterScheduledJob
{
    private const int PageSize = 100;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task ExecuteAsync(ClusterJobContext context, CancellationToken ct)
    {
        DataProduct? after = null;
        while (true)
        {
            var page = await products.ListScheduledProductsAsync(after, PageSize, ct).ConfigureAwait(false);
            await loader.ReprojectAgedAsync(page, ct).ConfigureAwait(false);
            foreach (var product in page)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsDue(product))
                {
                    continue;
                }

                try
                {
                    await loader.LoadAsync(product, ct).ConfigureAwait(false);
                }
                catch (ProductLoadException ex)
                {
                    logger.LogWarning("Scheduled load of data product {Product} failed with {Code}.", $"{product.Schema}.{product.Name}", ex.Code);
                }
            }

            if (page.Count < PageSize)
            {
                return;
            }

            after = page[^1];
        }
    }

    /// <summary>Due when there is no result yet, or the last load is at least one interval old; a recent attempt (even a failed one) is not repeated.</summary>
    internal bool IsDue(DataProduct product)
    {
        if (product.Load is not { Mode: LoadMode.Schedule, Interval: { } interval })
        {
            return false;
        }

        if (loader.AttemptedWithin(product, interval))
        {
            return false;
        }

        return loader.LoadedAt(product) is not { } at || _time.GetUtcNow() - at >= interval;
    }
}
