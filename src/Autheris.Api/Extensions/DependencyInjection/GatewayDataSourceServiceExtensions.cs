namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using Autheris.Application.DataSources;
using Autheris.Application.Observability.Telemetry;
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

        services.AddSingleton<IValidateOptions<DataSourceConfigurationOptions>>(
            new Autheris.Api.Configuration.Validation.DataSourceTransportRawValueValidator(section));

        services.TryAddSingleton<IDataSourceOptionsResolver, HierarchicalDataSourceOptionsResolver>();

        // Issue #447: ingress stamping and the fail-closed transport gate (enforcement call sites arrive with ).
        services.TryAddScoped<IIngressTransportContext, IngressTransportContext>();
        services.TryAddScoped<ITransportDenialRecorder, TransportDenialRecorder>();
        services.TryAddScoped<IDataSourceTransportGate, DataSourceTransportGate>();
        // Issue #447 warns about sources that allow no reachable transport (start and reload); never fails the start.
        services.AddHostedService(sp =>
        {
            var gateway = sp.GetRequiredService<IOptionsMonitor<Autheris.Domain.Options.GatewayOptions>>();
            return new Autheris.Api.Configuration.Validation.DataSourceTransportConfigurationReporter(
                sp.GetRequiredService<IOptionsMonitor<DataSourceConfigurationOptions>>(),
                () => gateway.CurrentValue,
                listener => gateway.OnChange(options => listener(options)),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Autheris.Api.Configuration.Validation.DataSourceTransportConfigurationReporter>>(),
                sp.GetService<IServiceScopeFactory>());
        });
        services.Replace(ServiceDescriptor.Singleton<IDataSourceTelemetryPolicyProvider, HierarchicalDataSourceTelemetryPolicyProvider>());

        return services;
    }
}
