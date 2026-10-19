namespace Autheris.Application.DataSources;

using System;
using Autheris.Application.Telemetry;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Options.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Hierarchical implementation of <see cref="IDataSourceTelemetryPolicyProvider"/> (BL-05).
/// Resolves per-source log levels, statement text logging, and telemetry policies via <see cref="IDataSourceOptionsResolver"/>,
/// clamped against the global <see cref="TelemetryCeiling"/> (SEC-DS05-06).
/// </summary>
public sealed class HierarchicalDataSourceTelemetryPolicyProvider : IDataSourceTelemetryPolicyProvider
{
    private readonly IDataSourceOptionsResolver _optionsResolver;
    private readonly IOptionsMonitor<TelemetryOptions> _telemetryOptions;

    public HierarchicalDataSourceTelemetryPolicyProvider(
        IDataSourceOptionsResolver optionsResolver,
        IOptionsMonitor<TelemetryOptions> telemetryOptions)
    {
        _optionsResolver = optionsResolver ?? throw new ArgumentNullException(nameof(optionsResolver));
        _telemetryOptions = telemetryOptions ?? throw new ArgumentNullException(nameof(telemetryOptions));
    }

    /// <inheritdoc />
    public DataSourceTelemetryPolicy GetPolicy(string dataSourceName)
    {
        var profile = _optionsResolver.Resolve(dataSourceName);
        var telemetry = _telemetryOptions.CurrentValue ?? new TelemetryOptions();
        var defaults = telemetry.DataSourceDefaults ?? new DataSourceTelemetryDefaults();
        var redaction = telemetry.Redaction ?? new RedactionOptions();

        if (!Enum.TryParse<LogLevel>(profile.MinimumLogLevel, ignoreCase: true, out var minLogLevel))
        {
            minLogLevel = LogLevel.Information;
        }

        var threshold = defaults.SlowQueryThresholdMilliseconds > 0
            ? TimeSpan.FromMilliseconds(defaults.SlowQueryThresholdMilliseconds)
            : (TimeSpan?)null;

        var stmtText = profile.LogGeneratedSql
            ? StatementTextMode.Parameterized
            : defaults.DefaultStatementText;

        var rawPolicy = new DataSourceTelemetryPolicy(
            MinimumLogLevel: minLogLevel,
            SpanDetail: defaults.DefaultSpanDetail,
            StatementText: stmtText,
            SlowQueryThreshold: threshold,
            RecordRowCounts: true);

        var ceiling = TelemetryCeiling.FromRedactionOptions(redaction);
        return rawPolicy.ClampTo(ceiling);
    }
}
