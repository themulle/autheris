namespace Autheris.Extensions.Cdc.CustomQuery;

using System;
using Autheris.Application.Streaming.Validation;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class CustomQueryPollerServiceExtensions
{
    public static IServiceCollection AddCustomQueryPoller(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<CustomQueryPollerOptions>(configuration.GetSection(CustomQueryPollerOptions.SectionName));
        services.TryAddSingleton<CustomQueryPollerValidator>();
        services.TryAddSingleton<IDataSourceConnectionFactory, DefaultDataSourceConnectionFactory>();
        services.AddHostedService<CustomQueryPollerService>();
        return services;
    }

    public static IServiceCollection AddCustomQueryPoller(
        this IServiceCollection services,
        Action<CustomQueryPollerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.TryAddSingleton<CustomQueryPollerValidator>();
        services.TryAddSingleton<IDataSourceConnectionFactory, DefaultDataSourceConnectionFactory>();
        services.AddHostedService<CustomQueryPollerService>();
        return services;
    }
}
