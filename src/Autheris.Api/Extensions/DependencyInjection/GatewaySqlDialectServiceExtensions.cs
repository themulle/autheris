namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using Autheris.Application.Sql.Dialects;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// WP-ARC-B-09: Registers SQL dialect providers and <see cref="ISqlDialectRegistry"/> in the DI container.
/// </summary>
public static class GatewaySqlDialectServiceExtensions
{
    public static IServiceCollection AddAutherisSqlDialects(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISqlDialectProvider, SqlServerDialectProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISqlDialectProvider, PostgreSqlDialectProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISqlDialectProvider, SqliteDialectProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISqlDialectProvider, OracleDialectProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISqlDialectProvider, DatabricksDialectProvider>());
        services.TryAddSingleton<ISqlDialectRegistry, SqlDialectRegistry>();

        return services;
    }
}
