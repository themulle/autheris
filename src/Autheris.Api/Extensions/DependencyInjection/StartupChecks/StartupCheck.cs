namespace Autheris.Api.Extensions.DependencyInjection.StartupChecks;

using System;
using System.Collections.Generic;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// One named startup check (ARCH-MNT-12). Checks are not DI services because validation runs before the container exists; they
/// live in one static ordered list and run fail-fast: the first check that throws wins and later checks do not run.
/// </summary>
internal sealed record StartupCheck(string Name, Action<StartupCheckContext> Run);

/// <summary>
/// The inputs shared by all checks. The danger bypass and warning lists are pure functions of the options and are computed once
/// (ARCH-MNT-14-SC-03).
/// </summary>
internal sealed class StartupCheckContext
{
    public StartupCheckContext(
        GatewayOptions options,
        IHostEnvironment environment,
        Func<string, string?> getEnvironmentVariable,
        ILogger? logger,
        TelemetryOptions? telemetry = null)
    {
        Options = options;
        Environment = environment;
        GetEnvironmentVariable = getEnvironmentVariable;
        Logger = logger;
        IsDevelopment = environment.IsDevelopment();
        DangerBypasses = options.GetActiveDangerBypasses();
        Warnings = options.GetActiveWarnings();
        Telemetry = telemetry;
        ExtraWarnings = BuildExtraWarnings(telemetry);
    }

    /// <summary>Telemetry options (<c>Gateway:Telemetry</c>), or null when the caller does not supply them.</summary>
    public TelemetryOptions? Telemetry { get; }

    /// <summary>
    /// WARN entries of option groups outside <see cref="GatewayOptions"/> (ARCH-MNT-06-FU-03). Reported in the startup banner only;
    /// the health component keeps reading <see cref="GatewayOptions.GetActiveWarnings"/>. Entries name the key, never a value.
    /// </summary>
    public IReadOnlyList<string> ExtraWarnings { get; }

    private static IReadOnlyList<string> BuildExtraWarnings(TelemetryOptions? telemetry) =>
        telemetry is { Enabled: true, Otlp.AllowInsecureTransport: true }
            ? [GatewayOptions.WarnPrefix + "otlp_allow_insecure_transport (Gateway:Telemetry:Otlp:AllowInsecureTransport)"]
            : [];

    public GatewayOptions Options { get; }

    public IHostEnvironment Environment { get; }

    public Func<string, string?> GetEnvironmentVariable { get; }

    public ILogger? Logger { get; }

    public bool IsDevelopment { get; }

    /// <summary>DANGER entries: blocked outside Development.</summary>
    public IReadOnlyList<string> DangerBypasses { get; }

    /// <summary>WARN entries: permitted everywhere but reported at startup.</summary>
    public IReadOnlyList<string> Warnings { get; }
}
