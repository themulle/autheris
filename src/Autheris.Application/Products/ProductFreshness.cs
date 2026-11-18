namespace Autheris.Application.Products;

using System;
using Autheris.Domain.Model;

public enum ProductFreshnessState { Fresh, Stale, TooStale, NotLoaded }

/// <summary>Age rules of a stage-1 result: <c>onDemand</c> ttl, the optional <c>MaxStaleness</c> and the stale marker of a failed load.</summary>
public static class ProductFreshness
{
    /// <summary>
    /// <c>NotLoaded</c> without a result; <c>TooStale</c> when <c>MaxStaleness</c> is set and the last successful load is older (reads are
    /// denied with <c>PRODUCT_STALE</c>); <c>Stale</c> when the product is marked stale or the ttl of an <c>onDemand</c> result has passed;
    /// otherwise <c>Fresh</c>. Without <c>MaxStaleness</c> the last result is served, marked stale.
    /// </summary>
    public static ProductFreshnessState Evaluate(DataProduct product, DateTimeOffset? loadedAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (loadedAt is not { } at)
        {
            return ProductFreshnessState.NotLoaded;
        }

        var age = now - at;
        if (product.Load.MaxStalenessMinutes is { } max && age > TimeSpan.FromMinutes(max))
        {
            return ProductFreshnessState.TooStale;
        }

        var ttlPassed = product.Load.Mode == LoadMode.OnDemand && age > TimeSpan.FromMinutes(product.Load.TtlMinutes);
        return product.Status == DataProductStatus.Stale || ttlPassed ? ProductFreshnessState.Stale : ProductFreshnessState.Fresh;
    }

    /// <summary>True when a product may be exposed as a catalog object: active, or stale and within <c>MaxStaleness</c>.</summary>
    public static bool IsExposable(DataProduct product, DateTimeOffset? loadedAt, DateTimeOffset now) =>
        product.Status switch
        {
            DataProductStatus.Active => true,
            DataProductStatus.Stale => Evaluate(product, loadedAt, now) != ProductFreshnessState.TooStale,
            _ => false
        };
}
