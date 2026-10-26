using System;
using System.Collections.Generic;
using System.Net;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Telemetry;

/// <summary>
/// ARCH-MNT-06 (SC-01, SC-02): fail-closed start-up check of every resolved OTLP exporter endpoint (traces, metrics, logs, including the
/// per-signal and global <c>OTEL_EXPORTER_OTLP_*</c> overrides). An invalid URI or a scheme other than http/https fails the start. A plaintext
/// <c>http</c> endpoint is accepted for a loopback host (<c>localhost</c>, <c>127.0.0.0/8</c>, <c>::1</c>, parsed, never by string prefix) and in
/// Development; anywhere else it fails unless <see cref="OtlpOptions.AllowInsecureTransport"/> is set (which logs a warning). Failure messages
/// name the option path and the signal only, never the endpoint value (it can carry credentials).
/// </summary>
public sealed class OtlpTransportOptionsValidator : IValidateOptions<TelemetryOptions>
{
    private static readonly string[] Signals = ["TRACES", "METRICS", "LOGS"];

    private readonly IHostEnvironment? _environment;
    private readonly ILogger<OtlpTransportOptionsValidator>? _logger;
    private readonly Func<TelemetryOptions, string, string?> _resolveEndpoint;

    public OtlpTransportOptionsValidator(
        IHostEnvironment? environment = null,
        ILogger<OtlpTransportOptionsValidator>? logger = null,
        Func<TelemetryOptions, string, string?>? resolveEndpoint = null)
    {
        _environment = environment;
        _logger = logger;
        _resolveEndpoint = resolveEndpoint ?? GatewayTelemetryServiceExtensions.ResolveOtlpEndpoint;
    }

    public ValidateOptionsResult Validate(string? name, TelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        var development = _environment?.IsDevelopment() == true;
        foreach (var signal in Signals)
        {
            var endpoint = _resolveEndpoint(options, signal);
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                continue;
            }

            if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                failures.Add($"The OTLP {signal.ToLowerInvariant()} endpoint (Gateway:Telemetry:Otlp:Endpoint or OTEL_EXPORTER_OTLP_*ENDPOINT) is not an absolute http or https URI.");
                continue;
            }

            if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(uri) && !development)
            {
                if (!options.Otlp.AllowInsecureTransport)
                {
                    failures.Add(
                        $"The OTLP {signal.ToLowerInvariant()} endpoint uses plaintext http to a non-loopback host. Use https, or set Gateway:Telemetry:Otlp:AllowInsecureTransport=true to accept plaintext export outside Development.");
                    continue;
                }

                _logger?.LogWarning(
                    "Gateway:Telemetry:Otlp:AllowInsecureTransport is enabled: the OTLP {Signal} export to a non-loopback host uses plaintext http.",
                    signal.ToLowerInvariant());
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    internal static bool IsLoopbackHost(Uri uri)
    {
        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var literal = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        return IPAddress.TryParse(literal, out var ip) && IPAddress.IsLoopback(ip);
    }
}
