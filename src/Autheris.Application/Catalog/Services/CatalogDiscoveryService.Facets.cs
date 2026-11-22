namespace Autheris.Application.Catalog.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Domain.Model;

public sealed partial class CatalogDiscoveryService
{
    public async Task<DiscoveryFacets> ListFacetsAsync(RequestContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (_discovery != null)
        {
            return await _discovery.FacetsAsync(access: null, CallerOf(context), ct).ConfigureAwait(false);
        }

        var (permitted, _) = await VisibleForAsync(context, ct).ConfigureAwait(false);
        return DiscoveryService.BuildFacets(permitted);
    }

    private async Task<IReadOnlyList<CatalogDatasetSummary>> ListAllDiscoveryDatasetsAsync(RequestContext context, CancellationToken ct)
    {
        var caller = CallerOf(context);
        var results = new List<CatalogDatasetSummary>();
        var after = 0;

        while (true)
        {
            var page = await _discovery!.ListAsync(
                new DiscoveryQuery(
                    Language: context.Language,
                    First: DiscoveryService.MaxPageSize,
                    After: after,
                    Surface: DiscoverySurface.Rest),
                caller,
                ct).ConfigureAwait(false);

            if (page.TotalCount > results.Capacity)
            {
                results.EnsureCapacity(page.TotalCount);
            }

            foreach (var obj in page.Items)
            {
                results.Add(CatalogDiscoveryProjection.ToDatasetSummary(obj, context.Language));
            }

            if (!page.HasNextPage || page.Items.Count == 0)
            {
                break;
            }

            after += page.Items.Count;
        }

        return results;
    }
}
