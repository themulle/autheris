namespace Autheris.Infrastructure.Telemetry;

using System;

/// <summary>
/// Metric view configurations defining histogram duration buckets and cardinality boundaries.
/// Enforces D-14 and SEC-OT06-08.
/// </summary>
public static class TelemetryViews
{
    public static readonly double[] DurationBucketsSeconds =
    [
        0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30
    ];

    public const int DefaultCardinalityLimit = 2000;
    public const int TenantCardinalityLimit = 500;
    public const int DataSourceCardinalityLimit = 200;

    /// <summary>
    /// Resolves explicit histogram boundaries for a given instrument name.
    /// </summary>
    public static double[]? BucketsFor(string instrumentName)
    {
        if (string.IsNullOrWhiteSpace(instrumentName))
        {
            return null;
        }

        if (instrumentName.EndsWith(".duration", StringComparison.OrdinalIgnoreCase) ||
            instrumentName.EndsWith("_duration", StringComparison.OrdinalIgnoreCase) ||
            instrumentName.Contains(".duration.", StringComparison.OrdinalIgnoreCase))
        {
            return DurationBucketsSeconds;
        }

        return null;
    }
}
