namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Registers the Governed REST Data API services and virtual system tables.
/// </summary>
public static class DataApiServiceExtensions
{
    public static IServiceCollection AddAutherisDataApi(this IServiceCollection services)
    {
        services.AddScoped<IGovernedDataQueryService, GovernedDataQueryService>();
        services.AddHostedService<VirtualSystemTablesHostedService>();
        return services;
    }
}

/// <summary>
/// Registers the virtual system tables into the metadata repository on gateway host startup.
/// </summary>
public sealed class VirtualSystemTablesHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VirtualSystemTablesHostedService> _logger;

    public VirtualSystemTablesHostedService(
        IServiceProvider serviceProvider,
        ILogger<VirtualSystemTablesHostedService> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetService<ITableMetadataRepository>();
            if (repository != null)
            {
                await VirtualSystemTables.RegisterSystemTablesAsync(repository, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Successfully registered virtual system tables (governance.system.*) in metadata repository.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register virtual system tables in metadata repository during startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
