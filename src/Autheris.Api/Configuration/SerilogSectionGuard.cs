namespace Autheris.Api.Configuration;

using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Startup guard ensuring legacy Serilog configuration sections are gracefully deprecated
/// and forbidden OTLP header keys are strictly rejected. Enforces D-12 and SEC-OT06-14.
/// </summary>
public static class SerilogSectionGuard
{
    public const string DeprecationWarningMessage =
        "WARNING: The 'Serilog' configuration section is deprecated and ignored in Autheris v1.2.5+ " +
        "(migrated to OpenTelemetry logging). Please configure log levels under 'Logging:LogLevel' " +
        "and OpenTelemetry exporters under 'Gateway:Telemetry:Otlp'.";

    public const string OtlpHeadersForbiddenMessage =
        "Configuring 'Gateway:Telemetry:Otlp:Headers' in appsettings is forbidden (SEC-OT06-14). " +
        "Configure exporter credentials via the standard environment variable 'OTEL_EXPORTER_OTLP_HEADERS' sourced from a Kubernetes Secret.";

    public static void ThrowIfConfigured(IConfiguration configuration) =>
        CheckConfiguration(configuration, null);

    public static void CheckConfiguration(IConfiguration configuration, TextWriter? warningWriter = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var serilogSection = configuration.GetSection("Serilog");
        if (serilogSection.Exists() && serilogSection.GetChildren().Any())
        {
            var allowLegacyEnv = Environment.GetEnvironmentVariable("AUTHERIS_ALLOW_LEGACY_CONFIG");
            var allowLegacyCfg = configuration["AUTHERIS_ALLOW_LEGACY_CONFIG"];
            var isStrictRejection = string.Equals(allowLegacyEnv, "false", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(allowLegacyCfg, "false", StringComparison.OrdinalIgnoreCase);

            if (isStrictRejection)
            {
                throw new InvalidOperationException(
                    "The 'Serilog' configuration section is no longer supported (removed in BL-06 in favor of OpenTelemetry logging). " +
                    "Please configure log levels under 'Logging:LogLevel' and OpenTelemetry exporters under 'Gateway:Telemetry:Otlp'.");
            }

            var writer = warningWriter ?? Console.Error;
            writer.WriteLine(DeprecationWarningMessage);
        }

        var headersSection = configuration.GetSection("Gateway:Telemetry:Otlp:Headers");
        if (headersSection.Exists() && (!string.IsNullOrWhiteSpace(headersSection.Value) || headersSection.GetChildren().Any()))
        {
            throw new InvalidOperationException(OtlpHeadersForbiddenMessage);
        }
    }
}
