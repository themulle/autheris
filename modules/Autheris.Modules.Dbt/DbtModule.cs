namespace Autheris.Modules.Dbt;

using System;
using System.Collections.Generic;
using Autheris.Modules.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// dbt (data build tool) integration module: manifest, governance and semantic-manifest ingestion, exposures, contract
/// validation, health circuit breaker and the verified dbt Cloud webhook handler.
/// </summary>
public sealed class DbtModule : IAutherisModule
{
    public string Id => "dbt";

    public IReadOnlyList<string> ConfigurationSections => ["Gateway:Dbt"];

    public void ConfigureServices(
        IServiceCollection services,
        IConfiguration moduleConfiguration,
        IModuleHostContext host)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(moduleConfiguration);
        ArgumentNullException.ThrowIfNull(host);

        services.AddDbtIntegration();
    }

    public void MapEndpoints(IModuleEndpointRegistrar endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        DbtEndpoints.MapEndpoints(endpoints);
    }
}
