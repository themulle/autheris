namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using Autheris.Application.DataSources;
using Autheris.Application.Telemetry;
using Autheris.Domain.Options.DataSources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

/// <summary>
/// Service collection extensions for registering granular per-data-source configuration (BL-05).
/// </summary>
public static class GatewayDataSourceServiceExtensions
{
    /// <summary>
    /// Registers hierarchical per-data-source configuration options, startup validator, options resolver,
    /// and replaces the global telemetry policy provider with the hierarchical provider.
    /// </summary>
    public static IServiceCollection AddDataSourceHierarchicalConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(DataSourceConfigurationOptions.SectionName);
        services.Configure<DataSourceConfigurationOptions>(section);

        services.TryAddSingleton<IValidateOptions<DataSourceConfigurationOptions>, DataSourceOptionsValidator>();

        services.TryAddSingleton<IDataSourceOptionsResolver, HierarchicalDataSourceOptionsResolver>();
        services.Replace(ServiceDescriptor.Singleton<IDataSourceTelemetryPolicyProvider, HierarchicalDataSourceTelemetryPolicyProvider>());

        return services;
    }
}
