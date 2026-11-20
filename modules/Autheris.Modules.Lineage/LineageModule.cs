namespace Autheris.Modules.Lineage;

using System;
using System.Collections.Generic;
using Autheris.Modules.Abstractions;
using Autheris.Modules.Lineage.Domain;
using Autheris.Modules.Lineage.Endpoints;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Optional OpenLineage export module loaded when <c>lineage</c> is enabled in <c>Gateway:Modules:Enabled</c>.
/// </summary>
public sealed class LineageModule : IAutherisModule
{
    public const string ModuleId = "lineage";
    public const string RouteGroupPrefix = "/modules/lineage";

    public string Id => ModuleId;

    public IReadOnlyList<string> ConfigurationSections => [LineageModuleOptions.SectionName];

    public void ConfigureServices(
        IServiceCollection services,
        IConfiguration moduleConfiguration,
        IModuleHostContext host)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(moduleConfiguration);
        ArgumentNullException.ThrowIfNull(host);

        services.AddLineageModule(moduleConfiguration);
    }

    public void MapEndpoints(IModuleEndpointRegistrar endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        LineageEndpoints.Map(endpoints);
    }
}
