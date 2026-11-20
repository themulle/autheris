namespace Autheris.Application.Catalog.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
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
}
