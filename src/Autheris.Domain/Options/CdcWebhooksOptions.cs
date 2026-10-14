namespace Autheris.Domain.Options;

/// <summary>
/// Options of the CDC webhook subscription endpoints (<c>/api/v1/cdc/subscriptions</c>) and the CDC webhook delivery service.
/// While disabled the endpoints answer 501 Not Implemented, no subscription store is registered
/// and no delivery service runs.
/// Bound to the configuration section <see cref="SectionName"/>.
/// </summary>
public sealed class CdcWebhooksOptions
{
    public const string SectionName = "Gateway:CdcWebhooks";

    /// <summary>Enables the CDC webhook subscription endpoints. Default: <c>false</c>.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Maximum number of subscriptions per tenant (hard limit, answered with 429).</summary>
    public int MaxSubscriptionsPerTenant { get; init; } = 100;

    /// <summary>How long the delivery service caches the subscriptions of one tenant.</summary>
    public int SubscriptionCacheSeconds { get; init; } = 5;

    /// <summary>
    /// Maximum age of the creator principal snapshot in days; an older snapshot denies delivery (the snapshot freezes
    /// roles and groups, so this bounds the effect of a creator losing a group). <c>0</c> disables the expiry.
    /// </summary>
    public int CreatorSnapshotMaxAgeDays { get; init; } = 90;

    /// <summary>Maximum number of group SIDs captured in a creator snapshot; more is rejected, never truncated.</summary>
    public int MaxSnapshotGroups { get; init; } = 256;

    /// <summary>Interval at which the delivery service flushes its aggregated audit batch events.</summary>
    public int AuditBatchSeconds { get; init; } = 30;
}
