namespace Autheris.Application.Products;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;

/// <summary>Persists the catalog object of a product (the table row, its columns and their PII classification).</summary>
public interface IProductCatalogStore
{
    /// <summary>
    /// Upserts the catalog object of <paramref name="product"/>: active while the product is exposable, inactive once it is retired, with
    /// every PII column carrying the PII classification. The catalog version is bumped by the catalog write itself (a deactivation or a
    /// declassification is restrictive and published at once).
    /// </summary>
    Task PersistAsync(DataProduct product, CancellationToken ct = default);
}

/// <summary>
/// Catalog persistence of data products over the regular catalog write path: the product object is an ordinary tenant-scoped table
/// (<c>products.t_&lt;tenanthash&gt;.&lt;schema&gt;__&lt;name&gt;</c>), so discovery, the decision point, the consent workflow and the TABLES reports
/// see it like any other object. The write is idempotent: repeating it after a failure converges to the same state.
/// </summary>
public sealed class ProductCatalogStore(
    ITableMetadataRepository metadata,
    IClassificationRepository classifications,
    TimeProvider? timeProvider = null) : IProductCatalogStore
{
    private readonly ITableMetadataRepository _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    private readonly IClassificationRepository _classifications = classifications ?? throw new ArgumentNullException(nameof(classifications));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task PersistAsync(DataProduct product, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        var now = _time.GetUtcNow();
        var projected = ProductCatalogProjector.Project(product, null, now);
        var stored = await _metadata.UpsertTableMetadataAsync(projected, ct).ConfigureAwait(false);
        await ProductColumnClassifications.SyncAsync(_classifications, product, stored, now, ct).ConfigureAwait(false);
    }
}
