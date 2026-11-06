namespace Autheris.Modules.Lakehouse;

using System;
using System.Collections.Generic;
using Autheris.Api.Endpoints;
using Autheris.Extensions.Lakehouse;
using Autheris.Modules.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Apache Iceberg REST Catalog and Delta Lake data source module.
/// </summary>
public sealed class LakehouseModule : IAutherisModule
{
    public string Id => "lakehouse";

    public IReadOnlyList<string> ConfigurationSections => ["Gateway:Lakehouse"];

    public void ConfigureServices(
        IServiceCollection services,
        IConfiguration moduleConfiguration,
        IModuleHostContext host)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(moduleConfiguration);
        ArgumentNullException.ThrowIfNull(host);

        services.AddLakehouseIntegration(moduleConfiguration);
    }

    public void ConfigureServices(
        IServiceCollection services,
        IConfiguration moduleConfiguration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(moduleConfiguration);

        services.AddLakehouseIntegration(moduleConfiguration);
    }

    public void MapEndpoints(IModuleEndpointRegistrar endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        IcebergRestCatalogEndpoints.MapEndpoints(endpoints);
    }
}
