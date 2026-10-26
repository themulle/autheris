namespace Autheris.Api.Extensions.DependencyInjection.StartupChecks;

using System;
using System.Collections.Generic;
using Autheris.Domain.Options;
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
        ILogger? logger)
    {
        Options = options;
        Environment = environment;
        GetEnvironmentVariable = getEnvironmentVariable;
        Logger = logger;
        IsDevelopment = environment.IsDevelopment();
        DangerBypasses = options.GetActiveDangerBypasses();
        Warnings = options.GetActiveWarnings();
    }

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
