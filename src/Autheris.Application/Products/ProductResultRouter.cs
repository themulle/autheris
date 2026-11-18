namespace Autheris.Application.Products;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Stage 2 routing of a caller query on a product object: the cached stage-1 result and nothing else.</summary>
public interface IProductResultRouter
{
    /// <summary>
    /// The routing of a query on <paramref name="table"/> for <paramref name="tenant"/>. The target is always the cached result of the
    /// product (loaded per its mode); a missing, retired or foreign product, a product without a result and a result beyond
    /// <c>MaxStaleness</c> fail with a <see cref="ProductLoadException"/>. There is no fallback to the base tables.
    /// </summary>
    Task<TableCacheRoutingDecision> RouteAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default);
}

public sealed class ProductResultRouter(IDataProductRepository products, ProductLoadService loader, ITableCacheCatalog catalog) : IProductResultRouter
{
    public async Task<TableCacheRoutingDecision> RouteAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenant.Value) || !ProductSource.TryParse(table, tenant, out var schema, out var name))
        {
            throw new ProductLoadException(ProductErrorCodes.Unavailable);
        }

        var product = await products.GetProductAsync(tenant, schema, name, ct).ConfigureAwait(false);
        if (product is null || product.Status == DataProductStatus.Retired || product.Kind != DataProductKind.View)
        {
            throw new ProductLoadException(ProductErrorCodes.Unavailable);
        }

        var result = await loader.GetResultAsync(product, ct).ConfigureAwait(false);
        if (!catalog.TryGet(result.CacheKey, out var metadata) || metadata is null || !metadata.IsHealthy)
        {
            throw new ProductLoadException(ProductErrorCodes.Unavailable);
        }

        return new TableCacheRoutingDecision(IsCached: true, CanSatisfyFully: true, InternalStorageTarget: metadata.ActiveViewName, Metadata: metadata);
    }
}
