namespace Autheris.Application.Products;

using System;
using Autheris.Application.Catalog.Semantic;
using Autheris.Domain.Common;

/// <summary>The reserved data source of data products: no catalog ingestion and no data source registration may use the name.</summary>
public static class ProductSource
{
    public const string Name = "products";

    public static bool IsProductSource(string? domain) => string.Equals(domain?.Trim(), Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Separator of schema and name inside the table name of the catalog identity; a product schema never contains it.</summary>
    public const string Separator = "__";

    /// <summary>
    /// The catalog object a product is exposed as: <c>products.t_&lt;tenanthash&gt;.&lt;schema&gt;__&lt;name&gt;</c>. The tenant schema is the one
    /// the semantic virtual tables use, so the global catalog and ownership model stays tenant-scoped.
    /// </summary>
    public static TableIdentifier ObjectOf(TenantId tenant, string schema, string name) =>
        new(Name, SemanticVirtualTables.TenantSchema(tenant), schema + Separator + name);

    /// <summary>
    /// The product a catalog identifier names, for <paramref name="tenant"/> only: the domain is the reserved product source, the schema is the
    /// tenant schema of the tenant and the table name splits at the first separator. Anything else names no product (fail closed).
    /// </summary>
    public static bool TryParse(TableIdentifier id, TenantId tenant, out string schema, out string name)
    {
        schema = name = string.Empty;
        if (!IsProductSource(id.Domain) || string.IsNullOrWhiteSpace(tenant.Value) ||
            !string.Equals(id.Schema, SemanticVirtualTables.TenantSchema(tenant), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var table = id.TableName;
        var at = table?.IndexOf(Separator, StringComparison.Ordinal) ?? -1;
        if (at <= 0 || at + Separator.Length >= table!.Length)
        {
            return false;
        }

        schema = table[..at].ToLowerInvariant();
        name = table[(at + Separator.Length)..].ToLowerInvariant();
        return true;
    }

    /// <summary>Key of the stage-1 result in the table cache: the catalog identity itself, so the result is served for exactly that object and tenant.</summary>
    public static TableIdentifier CacheKey(TenantId tenant, string schema, string name) => ObjectOf(tenant, schema, name);
}
