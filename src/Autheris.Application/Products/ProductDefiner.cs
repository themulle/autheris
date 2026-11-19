namespace Autheris.Application.Products;

using System;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// The identity a data product definition runs as in stage 1. Holding one is the only way to skip the consent decision of the base objects
/// in the federation path, so it can only be created by <see cref="ProductLoadService"/> (internal constructor, enforced by an architecture
/// test). It never skips admission, reserved names, the AST security visitor, read-only enforcement, timeouts, cost, row and byte limits
/// or auditing, and it is bound to the tenant of the product.
/// </summary>
public sealed class ProductDefiner
{
    internal ProductDefiner(DataProduct product, ProductLimits limits)
    {
        ArgumentNullException.ThrowIfNull(product);
        Product = product;
        Limits = limits;
        var identity = new ClaimsIdentity("ProductDefiner");
        identity.AddClaim(new Claim(ClaimTypes.PrimarySid, "product:" + product.Id.ToString("N")));
        identity.AddClaim(new Claim("tid", product.Tenant.Value));
        Principal = new ClaimsPrincipal(identity);
    }

    public DataProduct Product { get; }

    /// <summary>The effective limits of the load (product limit lowered to the global cap).</summary>
    public ProductLimits Limits { get; }

    public TenantId Tenant => Product.Tenant;

    /// <summary>The service principal the load is audited and metered under; it carries no roles and no groups.</summary>
    public ClaimsPrincipal Principal { get; }
}
