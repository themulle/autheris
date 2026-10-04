namespace Autheris.Application.ResourceGroups;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

public readonly record struct ResourceGroupLeaseResult(
    bool Success,
    ResourceGroupTier Tier,
    string? RejectionReason,
    IAsyncDisposable? Lease
)
{
    public static ResourceGroupLeaseResult Acquired(ResourceGroupTier tier, IAsyncDisposable lease) =>
        new(true, tier, null, lease);

    public static ResourceGroupLeaseResult QueueFull(ResourceGroupTier tier) =>
        new(false, tier, "QueueFull", null);

    public static ResourceGroupLeaseResult TimedOut(ResourceGroupTier tier) =>
        new(false, tier, "Timeout", null);
}

public interface IResourceGroupManager
{
    ValueTask<ResourceGroupLeaseResult> TryAcquireLeaseAsync(
        ResourceGroupTier tier,
        string tenantId,
        CancellationToken cancellationToken = default);

    ResourceGroupMetrics GetMetrics();
}
