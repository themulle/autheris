namespace Autheris.Modules.Abstractions;

using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Contract implemented by every external, out-of-core Autheris module loaded by the host at startup.
/// </summary>
public interface IAutherisModule
{
    /// <summary>
    /// Stable lowercase identifier for this module (e.g., <c>lakehouse</c>, <c>dbt</c>, <c>openmetadata</c>, <c>cdc</c>).
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Configuration section paths owned by this module (e.g., <c>Gateway:Lakehouse</c>).
    /// </summary>
    IReadOnlyList<string> ConfigurationSections { get; }

    /// <summary>
    /// Registers module services with the host DI container using the scoped module configuration and host context.
    /// </summary>
    void ConfigureServices(
        IServiceCollection services,
        IConfiguration moduleConfiguration,
        IModuleHostContext host);

    /// <summary>
    /// Maps module HTTP endpoints through the guarded module endpoint registrar.
    /// </summary>
    void MapEndpoints(IModuleEndpointRegistrar endpoints);
}
