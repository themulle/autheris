namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

public enum ResourceGroupTier
{
    Interactive = 1,
    AutonomousAgents = 2,
    BulkAnalytics = 3
}

public sealed record ResourceGroupTierConfig
{
    public ResourceGroupTier Tier { get; init; }
    public int MaxConcurrency { get; init; }
    public int MaxQueueDepth { get; init; }
    public TimeSpan Timeout { get; init; }

    public ResourceGroupTierConfig(ResourceGroupTier tier, int maxConcurrency, int maxQueueDepth, TimeSpan timeout)
    {
        if (maxConcurrency <= 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "MaxConcurrency must be positive.");
        if (maxQueueDepth < 0) throw new ArgumentOutOfRangeException(nameof(maxQueueDepth), "MaxQueueDepth cannot be negative.");
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");

        Tier = tier;
        MaxConcurrency = maxConcurrency;
        MaxQueueDepth = maxQueueDepth;
        Timeout = timeout;
    }

    public void Deconstruct(out ResourceGroupTier tier, out int maxConcurrency, out int maxQueueDepth, out TimeSpan timeout)
    {
        tier = Tier;
        maxConcurrency = MaxConcurrency;
        maxQueueDepth = MaxQueueDepth;
        timeout = Timeout;
    }

    public static ResourceGroupTierConfig DefaultFor(ResourceGroupTier tier) => tier switch
    {
        ResourceGroupTier.Interactive => new(tier, maxConcurrency: 50, maxQueueDepth: 20, timeout: TimeSpan.FromSeconds(5)),
        ResourceGroupTier.AutonomousAgents => new(tier, maxConcurrency: 10, maxQueueDepth: 50, timeout: TimeSpan.FromSeconds(15)),
        ResourceGroupTier.BulkAnalytics => new(tier, maxConcurrency: 5, maxQueueDepth: 100, timeout: TimeSpan.FromSeconds(60)),
        _ => throw new ArgumentOutOfRangeException(nameof(tier))
    };
}

public sealed record ResourceGroupTierMetrics(
    ResourceGroupTier Tier,
    int ActiveConcurrency,
    int MaxConcurrency,
    int QueuedRequests,
    int MaxQueueDepth,
    long TotalAcquired,
    long TotalRejectedQueueFull,
    long TotalRejectedTimeout
);

public sealed record ResourceGroupMetrics(
    DateTimeOffset Timestamp,
    IReadOnlyList<ResourceGroupTierMetrics> Tiers
);
