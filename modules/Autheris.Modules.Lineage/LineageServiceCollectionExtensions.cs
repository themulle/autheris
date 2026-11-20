namespace Autheris.Modules.Lineage;

using System;
using Autheris.Application.Lineage;
using Autheris.Application.Security;
using Autheris.Extensions.Lineage;
using Autheris.Modules.Lineage.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Registers OpenLineage export options and its hardened outbound HTTP client.
/// </summary>
public static class LineageServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="LineageModuleOptions"/> from <c>Gateway:Lineage</c> and registers
    /// <see cref="IOpenLineageClient"/> with <see cref="EgressIntegrations.Lineage"/> SSRF protection.
    /// </summary>
    public static IServiceCollection AddLineageModule(
        this IServiceCollection services,
        IConfiguration moduleConfiguration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(moduleConfiguration);

        services.AddOptions<LineageModuleOptions>()
            .Bind(moduleConfiguration.GetSection(LineageModuleOptions.SectionName));

        services.TryAddTransient<SsrfProtectionHandler>();
        services.AddHttpClient<OpenLineageClient>().AddSecureOutboundHandlers(EgressIntegrations.Lineage);
        services.TryAddScoped<IOpenLineageClient>(sp => sp.GetRequiredService<OpenLineageClient>());

        return services;
    }
}
