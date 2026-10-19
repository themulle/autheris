namespace Autheris.Tests.Unit.Telemetry;

using System;
using Autheris.Application.Telemetry;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Options.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DataSourceTelemetryPolicyTests
{
    [Fact]
    public void ClampTo_WhenExceedsStatementTextCeiling_ClampsToSummary()
    {
        var policy = new DataSourceTelemetryPolicy(
            MinimumLogLevel: LogLevel.Information,
            SpanDetail: SpanDetail.Detailed,
            StatementText: StatementTextMode.Parameterized,
            SlowQueryThreshold: TimeSpan.FromMilliseconds(500),
            RecordRowCounts: true);

        var ceiling = new TelemetryCeiling(AllowStatementText: false);
        var clamped = policy.ClampTo(ceiling);

        clamped.StatementText.ShouldBe(StatementTextMode.Summary);
        clamped.SpanDetail.ShouldBe(SpanDetail.Detailed);
    }

    [Fact]
    public void ClampTo_WhenExceedsSpanDetailCeiling_ClampsSpanDetail()
    {
        var policy = new DataSourceTelemetryPolicy(
            MinimumLogLevel: LogLevel.Information,
            SpanDetail: SpanDetail.Detailed,
            StatementText: StatementTextMode.Summary,
            SlowQueryThreshold: TimeSpan.FromMilliseconds(500),
            RecordRowCounts: true);

        var ceiling = new TelemetryCeiling(AllowStatementText: true, MaxSpanDetail: SpanDetail.Basic);
        var clamped = policy.ClampTo(ceiling);

        clamped.SpanDetail.ShouldBe(SpanDetail.Basic);
    }

    [Fact]
    public void ClampTo_WhenLogLevelTooLow_RaisesToCeilingMinimum()
    {
        var policy = new DataSourceTelemetryPolicy(
            MinimumLogLevel: LogLevel.Debug,
            SpanDetail: SpanDetail.Basic,
            StatementText: StatementTextMode.None,
            SlowQueryThreshold: null,
            RecordRowCounts: true);

        var ceiling = new TelemetryCeiling(AllowStatementText: false, MinimumLogLevel: LogLevel.Warning);
        var clamped = policy.ClampTo(ceiling);

        clamped.MinimumLogLevel.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public void ClampTo_WhenWithinCeiling_ReturnsSameInstance()
    {
        var policy = new DataSourceTelemetryPolicy(
            MinimumLogLevel: LogLevel.Warning,
            SpanDetail: SpanDetail.Basic,
            StatementText: StatementTextMode.Summary,
            SlowQueryThreshold: TimeSpan.FromMilliseconds(200),
            RecordRowCounts: true);

        var ceiling = new TelemetryCeiling(
            AllowStatementText: false,
            MaxSpanDetail: SpanDetail.Detailed,
            MinimumLogLevel: LogLevel.Information);

        var clamped = policy.ClampTo(ceiling);

        clamped.ShouldBeSameAs(policy);
    }

    [Fact]
    public void GlobalDataSourceTelemetryPolicyProvider_ResolvesAndUpdatesPolicy()
    {
        Action<TelemetryOptions, string?>? listener = null;
        var optionsMonitor = Substitute.For<IOptionsMonitor<TelemetryOptions>>();

        var initialOptions = new TelemetryOptions
        {
            DataSourceDefaults = new DataSourceTelemetryDefaults
            {
                MinimumLogLevel = "Information",
                DefaultSpanDetail = SpanDetail.Detailed,
                DefaultStatementText = StatementTextMode.Parameterized,
                SlowQueryThresholdMilliseconds = 1000
            },
            Redaction = new RedactionOptions
            {
                AllowStatementText = true
            }
        };

        optionsMonitor.CurrentValue.Returns(initialOptions);
        optionsMonitor.OnChange(Arg.Do<Action<TelemetryOptions, string?>>(a => listener = a))
            .Returns(Substitute.For<IDisposable>());

        using var provider = new GlobalDataSourceTelemetryPolicyProvider(optionsMonitor);

        var policy1 = provider.GetPolicy("mssql-main");
        policy1.MinimumLogLevel.ShouldBe(LogLevel.Information);
        policy1.SpanDetail.ShouldBe(SpanDetail.Detailed);
        policy1.StatementText.ShouldBe(StatementTextMode.Parameterized);
        policy1.SlowQueryThreshold.ShouldBe(TimeSpan.FromMilliseconds(1000));

        // Now simulate configuration change (e.g. statement text forbidden globally)
        var updatedOptions = new TelemetryOptions
        {
            DataSourceDefaults = initialOptions.DataSourceDefaults,
            Redaction = new RedactionOptions
            {
                AllowStatementText = false
            }
        };

        listener?.Invoke(updatedOptions, null);

        var policy2 = provider.GetPolicy("mssql-main");
        policy2.StatementText.ShouldBe(StatementTextMode.Summary);
    }
}
