namespace Autheris.Application.Dbt.Services;

using System.Security.Claims;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Tenancy rules of the persisted semantic catalog (#112 A1-3, SEC-112-13). The catalog is always keyed by tenant (DT-A-7); the default
/// tenant is the catalog owner and is only different in that its importer sees every physical table (metadata only, DT-SC-08).
/// </summary>
public sealed class SemanticTenancy
{
    private readonly TenantId? _catalogOwner;

    public SemanticTenancy(TenantId? catalogOwner) => _catalogOwner = catalogOwner;

    public static SemanticTenancy FromOptions(IOptions<GatewayOptions>? options) =>
        new(TenantResolver.EffectiveDefaultOf(options?.Value.Tenancy));

    /// <summary>The catalog owner (the default tenant); <c>null</c> when no default tenant is configured.</summary>
    public TenantId? CatalogOwner => _catalogOwner;

    /// <summary>True when <paramref name="tenant"/> is the catalog owner.</summary>
    public bool IsCatalogOwner(TenantId tenant) => CallerScopeResolver.IsOwner(tenant, _catalogOwner);

    /// <summary>Maps the caller's tenant to the store key: the tenant itself; an unset tenant gets no key.</summary>
    public bool TryMapKey(TenantId tenant, out TenantId key)
    {
        key = tenant;
        return tenant.Value is not null;
    }

    /// <summary>
    /// The tenant a caller may use for the semantic catalog, or <c>null</c> when the caller has no catalog standing: no SID (#138 rule)
    /// or no resolvable tenant (no claim and no default tenant, invalid claim). The result is still mapped by <see cref="TryMapKey"/>.
    /// </summary>
    public TenantId? ResolveCallerTenant(ClaimsPrincipal? principal)
    {
        var resolution = CallerScopeResolver.ResolutionOf(principal, _catalogOwner);
        if (CallerScopeResolver.Resolve(principal, resolution, _catalogOwner) == CallerScope.None)
        {
            return null;
        }

        return resolution.Tenant;
    }
}
