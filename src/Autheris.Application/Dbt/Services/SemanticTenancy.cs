namespace Autheris.Application.Dbt.Services;

using System.Security.Claims;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Tenancy rules of the persisted semantic catalog (#112 A1-3, SEC-112-13). The mode is read once at construction from
/// <c>Gateway:Tenancy:EffectiveMode</c>; every value other than <c>Single</c> (unset and unknown included) is Multi behaviour.
/// </summary>
public sealed class SemanticTenancy
{
    private readonly TenancyMode _mode;

    public SemanticTenancy(TenancyMode mode) => _mode = mode;

    public static SemanticTenancy FromOptions(IOptions<GatewayOptions>? options) =>
        new(options?.Value.Tenancy.EffectiveMode == TenancyMode.Single ? TenancyMode.Single : TenancyMode.Multi);

    public TenancyMode Mode => _mode;

    /// <summary>
    /// Maps the caller's tenant to the store key. Single: every caller shares the legacy row. Multi: the tenant itself; the legacy tenant
    /// id is never reachable (a claim-less caller, or a claim that resolves to it, gets no key).
    /// </summary>
    public bool TryMapKey(TenantId tenant, out TenantId key)
    {
        if (_mode == TenancyMode.Single)
        {
            key = TenantId.LegacySingleTenant;
            return true;
        }

        key = tenant;
        return tenant != TenantId.LegacySingleTenant;
    }

    /// <summary>
    /// The tenant a caller may use for the semantic catalog, or <c>null</c> when the caller has no catalog standing: no SID (#138 rule,
    /// both modes) and, in Multi, no tenant claim. The result is still mapped by <see cref="TryMapKey"/> in the catalog.
    /// </summary>
    public TenantId? ResolveCallerTenant(ClaimsPrincipal? principal)
    {
        var scope = CallerScopeResolver.Resolve(principal, _mode);
        if (scope is CallerScope.None or CallerScope.Claimless)
        {
            return null;
        }

        try
        {
            return principal!.GetTenantId();
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
    }
}
