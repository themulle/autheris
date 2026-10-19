namespace Autheris.Application.Telemetry;

using System;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Options.Observability;
using Microsoft.Extensions.Logging;

/// <summary>
/// Telemetry ceiling values enforced globally across all data sources.
/// Prevents per-source policies from exceeding security and verbosity limits.
/// </summary>
public readonly record struct TelemetryCeiling(
    bool AllowStatementText,
    SpanDetail MaxSpanDetail = SpanDetail.Detailed,
    LogLevel MinimumLogLevel = LogLevel.Trace)
{
    public static TelemetryCeiling FromRedactionOptions(RedactionOptions options) =>
        new(options.AllowStatementText);
}

/// <summary>
/// Policy governing telemetry emission for a specific data source.
/// </summary>
public sealed record DataSourceTelemetryPolicy(
    LogLevel MinimumLogLevel,
    SpanDetail SpanDetail,
    StatementTextMode StatementText,
    TimeSpan? SlowQueryThreshold,
    bool RecordRowCounts)
{
    /// <summary>
    /// Pure function returning a clamped policy that never exceeds the provided global ceiling.
    /// </summary>
    public DataSourceTelemetryPolicy ClampTo(in TelemetryCeiling ceiling)
    {
        var effectiveStatementText = StatementText;
        if (!ceiling.AllowStatementText && effectiveStatementText == StatementTextMode.Parameterized)
        {
            effectiveStatementText = StatementTextMode.Summary;
        }

        var effectiveSpanDetail = (SpanDetail)Math.Min((int)SpanDetail, (int)ceiling.MaxSpanDetail);
        var effectiveLogLevel = (LogLevel)Math.Max((int)MinimumLogLevel, (int)ceiling.MinimumLogLevel);

        if (effectiveStatementText == StatementText &&
            effectiveSpanDetail == SpanDetail &&
            effectiveLogLevel == MinimumLogLevel)
        {
            return this;
        }

        return new DataSourceTelemetryPolicy(
            effectiveLogLevel,
            effectiveSpanDetail,
            effectiveStatementText,
            SlowQueryThreshold,
            RecordRowCounts);
    }
}

/// <summary>
/// Provider for data source specific telemetry policies.
/// </summary>
public interface IDataSourceTelemetryPolicyProvider
{
    /// <summary>
    /// Gets the telemetry policy for the given data source name.
    /// Guaranteed to never throw and never return null (falls back to global defaults on unknown or invalid names).
    /// </summary>
    DataSourceTelemetryPolicy GetPolicy(string dataSourceName);
}
