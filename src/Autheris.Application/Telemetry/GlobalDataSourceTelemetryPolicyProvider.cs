namespace Autheris.Application.Telemetry;

using System;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Options.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Global default implementation of <see cref="IDataSourceTelemetryPolicyProvider"/>.
/// Recomputes and freezes the global policy on options change; returns it in O(1) without allocation.
/// </summary>
public sealed class GlobalDataSourceTelemetryPolicyProvider : IDataSourceTelemetryPolicyProvider, IDisposable
{
    private readonly IDisposable? _subscription;
    private volatile DataSourceTelemetryPolicy _currentPolicy;

    public GlobalDataSourceTelemetryPolicyProvider(IOptionsMonitor<TelemetryOptions> optionsMonitor)
    {
        ArgumentNullException.ThrowIfNull(optionsMonitor);
        _currentPolicy = BuildPolicy(optionsMonitor.CurrentValue);
        _subscription = optionsMonitor.OnChange(options =>
        {
            _currentPolicy = BuildPolicy(options);
        });
    }

    /// <inheritdoc />
    public DataSourceTelemetryPolicy GetPolicy(string dataSourceName) => _currentPolicy;

    private static DataSourceTelemetryPolicy BuildPolicy(TelemetryOptions? options)
    {
        var opts = options ?? new TelemetryOptions();
        var defaults = opts.DataSourceDefaults ?? new DataSourceTelemetryDefaults();
        var redaction = opts.Redaction ?? new RedactionOptions();

        if (!Enum.TryParse<LogLevel>(defaults.MinimumLogLevel, ignoreCase: true, out var minLogLevel))
        {
            minLogLevel = LogLevel.Information;
        }

        var threshold = defaults.SlowQueryThresholdMilliseconds > 0
            ? TimeSpan.FromMilliseconds(defaults.SlowQueryThresholdMilliseconds)
            : (TimeSpan?)null;

        var rawPolicy = new DataSourceTelemetryPolicy(
            MinimumLogLevel: minLogLevel,
            SpanDetail: defaults.DefaultSpanDetail,
            StatementText: defaults.DefaultStatementText,
            SlowQueryThreshold: threshold,
            RecordRowCounts: true);

        var ceiling = TelemetryCeiling.FromRedactionOptions(redaction);
        return rawPolicy.ClampTo(ceiling);
    }

    public void Dispose()
    {
        _subscription?.Dispose();
    }
}
