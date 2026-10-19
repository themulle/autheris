namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using System.Diagnostics;
using Autheris.Api.Telemetry;
using Autheris.Application.Telemetry;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Options.Observability;
using Autheris.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

/// <summary>
/// Service collection and logging builder extensions for OpenTelemetry composition.
/// Enforces WP-A1, WP-A2, and D-1 through D-18.
/// </summary>
public static class GatewayTelemetryServiceExtensions
{
    public static IServiceCollection AddGatewayTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var telemetrySection = configuration.GetSection(TelemetryOptions.SectionName);
        var telemetryOptions = telemetrySection.Get<TelemetryOptions>() ?? new TelemetryOptions();

        services.Configure<TelemetryOptions>(telemetrySection);
        services.AddDataSourceTelemetryPolicy();

        if (!telemetryOptions.Enabled)
        {
            return services;
        }

        DistributedContextPropagator.Current = AutherisDistributedContextPropagator.Instance;
        services.AddSingleton<DistributedContextPropagator>(AutherisDistributedContextPropagator.Instance);

        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: telemetryOptions.ServiceName,
                serviceNamespace: telemetryOptions.ServiceNamespace,
                serviceVersion: typeof(GatewayTelemetryServiceExtensions).Assembly.GetName().Version?.ToString());

        var otelBuilder = services.AddOpenTelemetry();

        otelBuilder.WithTracing(tracing =>
        {
            tracing.SetResourceBuilder(resourceBuilder);
            tracing.SetSampler(new AutherisSampler(
                telemetryOptions.Sampling.Ratio,
                telemetryOptions.Sampling.TrustParentSampling));

            foreach (var source in AutherisTelemetrySources.AllActivitySources)
            {
                tracing.AddSource(source);
            }

            tracing.AddSource("Microsoft.AspNetCore");

            tracing.AddProcessor(new SpanRedactionProcessor());
            tracing.AddProcessor(new TenantEnrichmentProcessor(telemetryOptions.IncludeTenant));

            var traceEndpoint = ResolveOtlpEndpoint(telemetryOptions, "TRACES");
            if (!string.IsNullOrWhiteSpace(traceEndpoint) && telemetryOptions.Otlp.Traces)
            {
                tracing.AddOtlpExporter(otlp =>
                {
                    otlp.Endpoint = new Uri(traceEndpoint);
                    otlp.Protocol = telemetryOptions.Otlp.Protocol == OtlpProtocol.Grpc
                        ? OtlpExportProtocol.Grpc
                        : OtlpExportProtocol.HttpProtobuf;
                    otlp.HttpClientFactory = OtlpHttpClientFactory.Create(telemetryOptions);
                });
            }
        });

        otelBuilder.WithMetrics(metrics =>
        {
            metrics.SetResourceBuilder(resourceBuilder);

            foreach (var meter in AutherisTelemetrySources.AllMeters)
            {
                metrics.AddMeter(meter);
            }

            metrics.AddMeter("Microsoft.AspNetCore.Hosting");
            metrics.AddMeter("Microsoft.AspNetCore.Server.Kestrel");
            metrics.AddMeter("System.Net.Http");

            var metricEndpoint = ResolveOtlpEndpoint(telemetryOptions, "METRICS");
            if (!string.IsNullOrWhiteSpace(metricEndpoint) && telemetryOptions.Otlp.Metrics)
            {
                metrics.AddOtlpExporter(otlp =>
                {
                    otlp.Endpoint = new Uri(metricEndpoint);
                    otlp.Protocol = telemetryOptions.Otlp.Protocol == OtlpProtocol.Grpc
                        ? OtlpExportProtocol.Grpc
                        : OtlpExportProtocol.HttpProtobuf;
                    otlp.HttpClientFactory = OtlpHttpClientFactory.Create(telemetryOptions);
                });
            }
        });

        return services;
    }

    public static ILoggingBuilder AddGatewayLogging(
        this ILoggingBuilder logging,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(configuration);

        var telemetrySection = configuration.GetSection(TelemetryOptions.SectionName);
        var telemetryOptions = telemetrySection.Get<TelemetryOptions>() ?? new TelemetryOptions();

        logging.ClearProviders();

        logging.AddOpenTelemetry(otelLogging =>
        {
            otelLogging.IncludeScopes = true;
            otelLogging.ParseStateValues = true;
            otelLogging.IncludeFormattedMessage = false;

            otelLogging.AddProcessor(new LogRedactionProcessor(telemetryOptions.IncludeTenant));
            otelLogging.AddProcessor(new BatchLogRecordExportProcessor(new JsonStdoutLogExporter()));

            var logEndpoint = ResolveOtlpEndpoint(telemetryOptions, "LOGS");
            if (!string.IsNullOrWhiteSpace(logEndpoint) && telemetryOptions.Otlp.Logs)
            {
                otelLogging.AddOtlpExporter(otlp =>
                {
                    otlp.Endpoint = new Uri(logEndpoint);
                    otlp.Protocol = telemetryOptions.Otlp.Protocol == OtlpProtocol.Grpc
                        ? OtlpExportProtocol.Grpc
                        : OtlpExportProtocol.HttpProtobuf;
                    otlp.HttpClientFactory = OtlpHttpClientFactory.Create(telemetryOptions);
                });
            }
        });

        return logging;
    }

    public static string? ResolveOtlpEndpoint(TelemetryOptions options, string signal)
    {
        var signalEnv = Environment.GetEnvironmentVariable($"OTEL_EXPORTER_OTLP_{signal}_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(signalEnv))
        {
            return signalEnv;
        }

        var globalEnv = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(globalEnv))
        {
            return globalEnv;
        }

        return options.Otlp.Endpoint;
    }
}
