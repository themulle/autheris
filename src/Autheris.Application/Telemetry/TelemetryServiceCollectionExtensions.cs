namespace Autheris.Application.Telemetry;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service registration extensions for Application layer telemetry services.
/// </summary>
public static class TelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the data source telemetry policy provider in the service container.
    /// </summary>
    public static IServiceCollection AddDataSourceTelemetryPolicy(this IServiceCollection services)
    {
        services.AddSingleton<IDataSourceTelemetryPolicyProvider, GlobalDataSourceTelemetryPolicyProvider>();
        return services;
    }
}
