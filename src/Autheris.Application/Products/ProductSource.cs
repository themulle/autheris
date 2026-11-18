namespace Autheris.Application.Products;

using System;
using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Common;

/// <summary>The reserved data source of data products: no catalog ingestion and no data source registration may use the name.</summary>
public static class ProductSource
{
    public const string Name = "products";

    public static bool IsProductSource(string? domain) => string.Equals(domain?.Trim(), Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The catalog object a product is exposed as: <c>products.&lt;schema&gt;.&lt;name&gt;</c>.</summary>
    public static TableIdentifier ObjectOf(string schema, string name) => new(Name, schema, name);

    /// <summary>
    /// Key of the stage-1 result in the table cache. Products are tenant-scoped while the cache is global, so the tenant is part of the
    /// key (a short hash, because an identifier component cannot hold every tenant id character).
    /// </summary>
    public static TableIdentifier CacheKey(TenantId tenant, string schema, string name)
    {
        var tenantKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tenant.Value)))[..16];
        return new TableIdentifier(Name, schema, $"{name}__{tenantKey}");
    }
}
