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
}
