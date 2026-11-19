namespace Autheris.Application.Products;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State;
using Autheris.Domain.Common;

/// <summary>
/// Per-product call limit of write procedures: a fixed window per tenant and product, counted in the cluster state (one counter per window
/// with a TTL, so no window outlives its use and every node shares the limit). It bounds the damage a runaway or abusive caller can do
/// with a granted write procedure; the database rights of the connection stay the hard limit.
/// </summary>
public static class ProductWriteRateLimiter
{
    public const int WindowSeconds = 60;

    public const int MaxCallsPerWindow = 60;

    internal const string KeyPrefix = "products:write-rate:";

    /// <summary>
    /// True when the call fits the window, false when the limit is reached, null when the cluster state is unavailable (the caller fails
    /// closed: no write runs without a counter).
    /// </summary>
    public static async Task<bool?> TryAcquireAsync(
        IDistributedClusterStateProvider? state, TenantId tenant, Guid productId, DateTimeOffset now, CancellationToken ct = default)
    {
        if (state is null)
        {
            return null;
        }

        var window = now.ToUnixTimeSeconds() / WindowSeconds;
        try
        {
            var count = await state.IncrementAsync($"{KeyPrefix}{tenant.Value}:{productId:N}:{window}", 1, TimeSpan.FromSeconds(WindowSeconds * 2), ct).ConfigureAwait(false);
            return count is null ? null : count <= MaxCallsPerWindow;
        }
        catch (ClusterStateUnavailableException)
        {
            return null;
        }
    }
}
