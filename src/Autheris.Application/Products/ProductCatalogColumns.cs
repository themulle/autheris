namespace Autheris.Application.Products;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;

/// <summary>Columns of a catalog object with their PII state, read from the stored catalog only (never from the source).</summary>
public interface IProductCatalogColumns
{
    /// <summary>The columns of the object, or null when the object is unknown (or belongs to another tenant).</summary>
    Task<IReadOnlyList<CatalogColumnInfo>?> GetColumnsAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default);
}

public sealed class ProductCatalogColumns(ITableMetadataRepository metadata) : IProductCatalogColumns
{
    private readonly ITableMetadataRepository _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));

    public async Task<IReadOnlyList<CatalogColumnInfo>?> GetColumnsAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default)
    {
        var meta = await _metadata.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
        if (meta is null || !meta.Table.IsActive ||
            (!string.IsNullOrEmpty(meta.OwnerTenantId) && !string.Equals(meta.OwnerTenantId, tenant.Value, StringComparison.Ordinal)))
        {
            return null;
        }

        // Hidden columns count as PII: the mapper reports hidden columns as non-PII because it hides them, which is the wrong direction here.
        return meta.Columns
            .Select(c => new CatalogColumnInfo(
                c.ColumnName,
                c.DataType,
                c.DiscoveryHidden || DiscoveryAccessMapper.ResolveColumnClassifications(meta, c, isHidden: false, tenant.Value).Pii))
            .ToList();
    }
}
