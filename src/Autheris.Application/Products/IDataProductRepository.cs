namespace Autheris.Application.Products;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public enum ProductWriteStatus { Created, Replaced, AlreadyExists, NotFound, VersionMismatch }

public sealed record ProductWriteOutcome(ProductWriteStatus Status, DataProduct? Product = null);

/// <summary>
/// Tenant-scoped store of data products, their definition history and the owners of data sources. Every statement carries the tenant
/// predicate; a blank tenant throws. <c>Version</c> is the revision counter of the product row (optimistic concurrency).
/// </summary>
public interface IDataProductRepository
{
    Task<DataProduct?> GetProductAsync(TenantId tenant, string schema, string name, CancellationToken ct = default);

    /// <summary>Products of the tenant ordered by schema and name; <paramref name="afterKey"/> is the <c>schema/name</c> of the last item of the previous page.</summary>
    Task<IReadOnlyList<DataProduct>> ListProductsAsync(TenantId tenant, string? afterKey, int limit, CancellationToken ct = default);

    /// <summary>Inserts the product with its first definition revision; <see cref="ProductWriteStatus.AlreadyExists"/> when the schema and name are taken.</summary>
    Task<ProductWriteOutcome> CreateProductAsync(DataProduct product, DataProductVersion firstRevision, CancellationToken ct = default);

    /// <summary>
    /// Replaces the product row when its stored version equals <paramref name="expectedVersion"/> and increments the version;
    /// <paramref name="definitionRevision"/> (when the definition changed) is stored as a new history row.
    /// </summary>
    Task<ProductWriteOutcome> ReplaceProductAsync(DataProduct product, int expectedVersion, DataProductVersion? definitionRevision, CancellationToken ct = default);

    Task<IReadOnlyList<DataProductVersion>> GetProductVersionsAsync(TenantId tenant, System.Guid productId, CancellationToken ct = default);

    Task<bool> IsDataSourceOwnerAsync(TenantId tenant, string dataSource, Sid ownerSid, CancellationToken ct = default);

    Task<IReadOnlyList<DataSourceOwner>> GetDataSourceOwnersAsync(TenantId tenant, string dataSource, CancellationToken ct = default);

    /// <summary>Adds the owner; false when the same owner was already assigned.</summary>
    Task<bool> AddDataSourceOwnerAsync(DataSourceOwner owner, CancellationToken ct = default);

    Task<bool> RemoveDataSourceOwnerAsync(TenantId tenant, string dataSource, Sid ownerSid, CancellationToken ct = default);
}
