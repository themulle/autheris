namespace Autheris.Application.Products;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>Keeps the data owner of the catalog object of a product equal to the product owner of the product.</summary>
public interface IProductOwnershipSync
{
    Task SyncAsync(DataProduct product, CancellationToken ct = default);
}

/// <summary>
/// Persistence seam of the owner link: sets the data owner of a product catalog object (null removes every owner). It writes the owner
/// of the tenant-scoped object only and never creates a second owner.
/// </summary>
public interface IProductCatalogOwnerStore
{
    Task SetOwnerAsync(Autheris.Domain.Common.TenantId tenant, Autheris.Domain.Common.TableIdentifier catalogObject, Autheris.Domain.Common.Sid? owner, CancellationToken ct = default);
}

/// <summary>
/// The product owner group is recorded as the owner of <c>products.t_&lt;tenanthash&gt;.&lt;schema&gt;__&lt;name&gt;</c>, so catalog metadata and
/// reports stay synchronized. <c>DATA_PRODUCTS.owner_group_sid</c> stays the source of truth: a run replaces the owner; a retired product has none.
/// </summary>
public sealed class ProductOwnershipSync(IProductCatalogOwnerStore store) : IProductOwnershipSync
{
    public async Task SyncAsync(DataProduct product, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        try
        {
            await store.SetOwnerAsync(
                product.Tenant,
                ProductSource.ObjectOf(product.Tenant, product.Schema, product.Name),
                product.Status == DataProductStatus.Retired ? (Autheris.Domain.Common.Sid?)null : product.OwnerGroupSid,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ProductException(ProductFailure.Conflict, ProductErrorCodes.OwnerSyncFailed);
        }
    }
}
