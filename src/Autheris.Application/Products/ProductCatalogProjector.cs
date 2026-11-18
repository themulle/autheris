namespace Autheris.Application.Products;

using System;
using System.Linq;
using Autheris.Domain.Model;

/// <summary>Projects a product onto the catalog object the decision point decides: <c>products.t_&lt;tenanthash&gt;.&lt;schema&gt;__&lt;name&gt;</c>.</summary>
public static class ProductCatalogProjector
{
    /// <summary>
    /// The catalog object of an exposable product. Columns that can carry PII are marked sensitive; the object is inactive (so every
    /// decision denies) unless <see cref="ProductFreshness.IsExposable"/> holds.
    /// </summary>
    public static TableMetadata Project(DataProduct product, DateTimeOffset? loadedAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(product);
        var identifier = ProductSource.ObjectOf(product.Tenant, product.Schema, product.Name);
        var table = new Table
        {
            Id = product.Id,
            SourceName = ProductSource.Name,
            SchemaName = identifier.Schema,
            TableName = identifier.TableName,
            DisplayName = product.Name,
            Description = product.Description,
            IsActive = ProductFreshness.IsExposable(product, loadedAt, now),
            ObjectKind = DiscoveryObjectKind.Table
        };
        return new TableMetadata
        {
            Table = table,
            Identifier = identifier,
            OwnerTenantId = product.Tenant.Value,
            Columns = product.Columns.Select(c => new TableColumn
            {
                TableId = product.Id,
                ColumnName = c.Name,
                DataType = c.DataType,
                IsSensitive = c.Class != ProductColumnClass.NonPii
            }).ToList()
        };
    }
}
