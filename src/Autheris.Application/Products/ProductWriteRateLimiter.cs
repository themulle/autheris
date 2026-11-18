namespace Autheris.Application.Products;

using System;
using System.Collections.Concurrent;
using Autheris.Domain.Common;

/// <summary>
/// Per-product call limit of write procedures: a fixed window per tenant and product, held in this process. It bounds the damage a runaway
/// or abusive caller can do with a granted write procedure; the database rights of the connection stay the hard limit.
/// </summary>
public static class ProductWriteRateLimiter
{
    public const int WindowSeconds = 60;

    public const int MaxCallsPerWindow = 60;

    private static readonly ConcurrentDictionary<(string Tenant, Guid Product), (long Window, int Count)> Windows = new();

    public static bool TryAcquire(TenantId tenant, Guid productId) => TryAcquire(tenant, productId, TimeProvider.System.GetUtcNow());

    internal static bool TryAcquire(TenantId tenant, Guid productId, DateTimeOffset now)
    {
        var window = now.ToUnixTimeSeconds() / WindowSeconds;
        var allowed = true;
        Windows.AddOrUpdate(
            (tenant.Value, productId),
            _ => (window, 1),
            (_, current) =>
            {
                if (current.Window != window)
                {
                    return (window, 1);
                }

                allowed = current.Count < MaxCallsPerWindow;
                return allowed ? (window, current.Count + 1) : current;
            });
        return allowed;
    }
}
