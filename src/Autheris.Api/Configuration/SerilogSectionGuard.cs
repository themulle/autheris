namespace Autheris.Api.Configuration;

using System;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Startup guard ensuring legacy Serilog configuration sections and forbidden OTLP header keys are rejected.
/// Enforces D-12 and SEC-OT06-14.
/// </summary>
public static class SerilogSectionGuard
{
    public static void ThrowIfConfigured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var serilogSection = configuration.GetSection("Serilog");
        if (serilogSection.Exists() && serilogSection.GetChildren().Any())
        {
            throw new InvalidOperationException(
                "The 'Serilog' configuration section is no longer supported (removed in BL-06 in favor of OpenTelemetry logging). " +
                "Please configure log levels under 'Logging:LogLevel' and OpenTelemetry exporters under 'Gateway:Telemetry:Otlp'.");
        }

        var headersSection = configuration.GetSection("Gateway:Telemetry:Otlp:Headers");
        if (headersSection.Exists() && (!string.IsNullOrWhiteSpace(headersSection.Value) || headersSection.GetChildren().Any()))
        {
            throw new InvalidOperationException(
                "Configuring 'Gateway:Telemetry:Otlp:Headers' in appsettings is forbidden (SEC-OT06-14). " +
                "Configure exporter credentials via the standard environment variable 'OTEL_EXPORTER_OTLP_HEADERS' sourced from a Kubernetes Secret.");
        }
    }
}
