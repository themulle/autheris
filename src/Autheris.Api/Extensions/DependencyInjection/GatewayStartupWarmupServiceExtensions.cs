namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Api.Hosting.Warmup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    internal static void CaptureColdPathTiming(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton(new ColdPathTimingFlag(configuration.GetValue<bool>($"{ColdPathTimingOptions.SectionName}:Enabled")));

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

        return services;
    }
}
