namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Api.Hosting.Warmup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

/// <summary>PLAN-OPS-150 (D-150-7): registration of the startup warm-up and cold-path measurement pieces.</summary>
public static class GatewayStartupWarmupServiceExtensions
{
    /// <summary>The cold-path timing switch as read from configuration while the services were being registered.</summary>
    private sealed record ColdPathTimingFlag(bool Enabled);

    /// <summary>
    /// Stores the <c>Gateway:Startup:ColdPathTiming:Enabled</c> switch once (a host builder registers <c>IConfiguration</c>
    /// through a factory, so the instance cannot be read back from the service collection).
    /// </summary>
    internal static void CaptureColdPathTiming(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new ColdPathTimingFlag(configuration.GetValue<bool>($"{ColdPathTimingOptions.SectionName}:Enabled")));
        // The Enabled switch is read here, at registration, because whether the hosted service exists is a registration-time decision.
        // StartupWarmupOptions.Enabled is bound for validation and documentation but is not read at runtime, so there are two sources
        // of truth by design. A missing key means enabled (fail-closed default).
        services.AddSingleton(new StartupWarmupFlag(configuration.GetValue($"{StartupWarmupOptions.SectionName}:Enabled", true)));
    }

    /// <summary>The best-effort warm-up switch as read from configuration while the services were being registered.</summary>
    private sealed record StartupWarmupFlag(bool Enabled);

    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Optional: captures the switch when the caller did not go through <c>AddGatewayOptions</c>.</param>
    public static IServiceCollection AddAutherisStartupWarmup(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configuration is not null)
        {
            CaptureColdPathTiming(services, configuration);
        }

        services.AddOptions<ColdPathTimingOptions>()
            .BindConfiguration(ColdPathTimingOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ColdPathTimingOptions>, ColdPathTimingOptionsValidator>();

        // The listener is only registered when explicitly enabled (default off, SEC-OPS150-12).
        var enabled = services.LastOrDefault(d => d.ServiceType == typeof(ColdPathTimingFlag))?.ImplementationInstance is ColdPathTimingFlag { Enabled: true };
        if (enabled)
        {
            services.AddSingleton<ColdPathTimingListener>();
            services
                .AddGraphQLServer()
                .AddApplicationService<ColdPathTimingListener>()
                .AddDiagnosticEventListener(sp => sp.GetRequiredService<ColdPathTimingListener>());
        }

        AddWarmupFramework(services);
        AddGraphQlExecutorGate(services);

        return services;
    }

    private static void AddWarmupFramework(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<StartupWarmupOptions>()
            .BindConfiguration(StartupWarmupOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<StartupWarmupOptions>, StartupWarmupOptionsValidator>();

        // Fail-closed default: a missing flag means enabled. Disabled registers no hosted service and reports Ready for the
        // best-effort part only (A-150-3); the executor gate is registered separately and unconditionally.
        var enabled = services.LastOrDefault(d => d.ServiceType == typeof(StartupWarmupFlag))?.ImplementationInstance is not StartupWarmupFlag { Enabled: false };
        var state = new StartupWarmupState(enabled ? WarmupPhase.Pending : WarmupPhase.Ready);
        services.AddSingleton(state);
        services.AddSingleton<IStartupWarmupState>(state);
        if (enabled)
        {
            services.AddHostedService<StartupWarmupService>();
        }
    }

    /// <summary>A-150-3: registered unconditionally, independent of <see cref="StartupWarmupOptions"/>.</summary>
    private static void AddGraphQlExecutorGate(IServiceCollection services)
    {
        services.AddSingleton<GraphQlExecutorGate>();
        services.AddSingleton<IGraphQlExecutorGate>(sp => sp.GetRequiredService<GraphQlExecutorGate>());
        services.AddHostedService(sp => sp.GetRequiredService<GraphQlExecutorGate>());
    }
}
