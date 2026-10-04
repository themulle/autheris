namespace Autheris.Extensions.Lineage;

using System.Net.Http;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

public static class LineageExportServiceCollectionExtensions
{
    public const string OpenJevHttpClientName = "OpenJev";

    /// <summary>
    /// External lineage / AI triage endpoints: OpenLineage (Marquez, Atlas, ...) export client and the OpenJEV
    /// justification classifier. The internal lineage graph store stays in the core.
    /// </summary>
    public static IServiceCollection AddLineageExportIntegration(this IServiceCollection services, GatewayOptions gatewayOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(gatewayOptions);

        if (!ExtensionRegistration.TryBegin<LineageExportIntegrationMarker>(services))
        {
            return services;
        }

        services.TryAddTransient<SsrfProtectionHandler>();

        // SEC EX-12: IOpenLineageClient is resolved through the typed client (with SSRF handler) instead of a
        // type-based registration that would receive the default HttpClient.
        services.AddHttpClient<OpenLineageClient>().AddSecureOutboundHandlers(EgressIntegrations.Lineage);
        services.TryAddScoped<IOpenLineageClient>(sp => sp.GetRequiredService<OpenLineageClient>());

        services.AddHttpClient(OpenJevHttpClientName).AddSecureOutboundHandlers(EgressIntegrations.Lineage);
        services.TryAddSingleton<IOpenJevClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var logger = sp.GetRequiredService<ILogger<OpenJevClient>>();
            return new OpenJevClient(logger, factory.CreateClient(OpenJevHttpClientName));
        });

        return services;
    }

    private sealed class LineageExportIntegrationMarker
    {
    }
}
