namespace Autheris.Api.Configuration;

using System;
using System.Linq;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Startup guard: the removed <c>Serilog</c> configuration section (BL-06, replaced by OpenTelemetry logging) and the forbidden OTLP header
/// keys (SEC-OT06-14) fail startup. There is no switch that downgrades either to a warning (L1). Messages name keys, never values.
/// </summary>
public static class SerilogSectionGuard
{
    public const string SerilogSectionRemovedMessage =
        "The 'Serilog' configuration section is no longer supported (removed in BL-06 in favor of OpenTelemetry logging). " +
        "Please configure log levels under 'Logging:LogLevel' and OpenTelemetry exporters under 'Gateway:Telemetry:Otlp'.";

    public const string OtlpHeadersForbiddenMessage =
        "Configuring 'Gateway:Telemetry:Otlp:Headers' in appsettings is forbidden (SEC-OT06-14). " +
        "Configure exporter credentials via the standard environment variable 'OTEL_EXPORTER_OTLP_HEADERS' sourced from a Kubernetes Secret.";

    public static void ThrowIfConfigured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var serilogSection = configuration.GetSection("Serilog");
        if (serilogSection.Exists() && serilogSection.GetChildren().Any())
        {
            throw new InvalidOperationException(SerilogSectionRemovedMessage);
        }

        var headersSection = configuration.GetSection("Gateway:Telemetry:Otlp:Headers");
        if (headersSection.Exists() && (!string.IsNullOrWhiteSpace(headersSection.Value) || headersSection.GetChildren().Any()))
        {
            throw new InvalidOperationException(OtlpHeadersForbiddenMessage);
        }
    }
}
