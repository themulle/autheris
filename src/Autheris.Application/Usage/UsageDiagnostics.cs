namespace Autheris.Application.Usage;

using System.Diagnostics.Metrics;
using Autheris.Domain.Diagnostics;

public static class UsageDiagnostics
{
    public static readonly Counter<long> UnscopedCounter =
        GatewayDiagnostics.Meter.CreateCounter<long>(
            "autheris_usage_unscoped_total",
            description: "Standalone usage events recorded without an ambient request scope");

    public static readonly Counter<long> RecordErrorsCounter =
        GatewayDiagnostics.Meter.CreateCounter<long>(
            "autheris_usage_record_errors_total",
            description: "Exceptions caught and suppressed in the usage recorder");

    public static readonly Counter<long> GroupsTruncatedCounter =
        GatewayDiagnostics.Meter.CreateCounter<long>(
            "autheris_usage_groups_truncated_total",
            description: "Number of times a principal's group set was truncated to MaxGroupsPerUser");

    public static readonly Counter<long> SeriesOverflowCounter =
        GatewayDiagnostics.Meter.CreateCounter<long>(
            "autheris_usage_series_overflow_total",
            description: "Usage series keys beyond capacity folded into other bucket");

    public static readonly Counter<long> DroppedCounter =
        GatewayDiagnostics.Meter.CreateCounter<long>(
            "autheris_usage_dropped_total",
            description: "Usage events dropped because capacity and headroom were exceeded");

    public static readonly Counter<long> AllowWithoutGrantCounter =
        GatewayDiagnostics.Meter.CreateCounter<long>(
            "autheris_usage_allow_without_grant_total",
            description: "Allowed usage facts without any contributing grant recorded");

    public static readonly Counter<long> SourceUnitMismatchCounter =
        GatewayDiagnostics.Meter.CreateCounter<long>(
            "autheris_usage_source_unit_mismatch_total",
            description: "Facts whose source cost unit differs from the expected tariff unit");

    public static readonly Counter<long> GrantValidityConflictCounter =
        GatewayDiagnostics.Meter.CreateCounter<long>(
            "autheris_usage_grant_validity_conflict_total",
            description: "Accumulated grants whose validity window differs from the stored reference");

    public static readonly Counter<long> UnattributedCounter =
        GatewayDiagnostics.Meter.CreateCounter<long>(
            "autheris_usage_unattributed_total",
            description: "Anonymous or unauthenticated requests not attributed to a usage scope");

    public static readonly Counter<long> SourceCostExpiredCounter =
        GatewayDiagnostics.Meter.CreateCounter<long>(
            "autheris_usage_source_cost_expired_total",
            description: "Pending source queries dropped after exceeding the maximum collection age");

    public static readonly Counter<long> SourceCostQueueOverflowCounter =
        GatewayDiagnostics.Meter.CreateCounter<long>(
            "autheris_usage_source_cost_queue_overflow_total",
            description: "Pending source queries dropped because the collection queue reached capacity");

    public static readonly Counter<long> SourceCostMalformedCounter =
        GatewayDiagnostics.Meter.CreateCounter<long>(
            "autheris_usage_source_cost_malformed_total",
            description: "Source cost samples rejected due to invalid or out-of-bounds values");

    public static readonly Counter<long> SourceCostCollectedCounter =
        GatewayDiagnostics.Meter.CreateCounter<long>(
            "autheris_usage_source_cost_collected_total",
            description: "Delayed source cost samples successfully folded into usage counters");

    public static readonly Counter<long> SourceCostErrorsCounter =
        GatewayDiagnostics.Meter.CreateCounter<long>(
            "autheris_usage_source_cost_errors_total",
            description: "Source cost collection batches that failed and were scheduled for retry");
}
