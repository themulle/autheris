namespace Autheris.Extensions.OpenMetadata;

using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class OpenMetadataServiceCollectionExtensions
{
    /// <summary>
    /// OpenMetadata governance integration (policy/role/team sync). The REST client is always registered (it is also
    /// used by the OpenMetadata data catalog adapter); the periodic sync worker only when <c>Gateway:OpenMetadata:Enabled</c> is true.
    /// </summary>
    public static IServiceCollection AddOpenMetadataIntegration(this IServiceCollection services, GatewayOptions gatewayOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(gatewayOptions);

        if (!ExtensionRegistration.TryBegin<OpenMetadataIntegrationMarker>(services))
        {
            return services;
        }

        AddOpenMetadataHttpClient(services);
        services.TryAddScoped<IOpenMetadataSyncService, OpenMetadataSyncService>();

        if (gatewayOptions.OpenMetadata.Enabled)
        {
            services.AddHostedService<OpenMetadataSyncBackgroundService>();
        }

        return services;
    }

    /// <summary>
    /// SEC EX-12: registers the typed client <see cref="IOpenMetadataClient"/> together with the SSRF handler. The handler must be
    /// attached to the builder returned by <c>AddHttpClient&lt;IOpenMetadataClient, OpenMetadataClient&gt;()</c> (client name
    /// "IOpenMetadataClient"); attaching it to a separately named "OpenMetadataClient" client has no effect.
    /// SEC E-03: hardened primary handler (no redirects, connect-time IP check) via <see cref="SecureOutboundHttp"/>.
    /// </summary>
    internal static void AddOpenMetadataHttpClient(IServiceCollection services)
    {
        if (!ExtensionRegistration.TryBegin<OpenMetadataHttpClientMarker>(services))
        {
            return;
        }

        services.TryAddTransient<SsrfProtectionHandler>();
        services.AddHttpClient<IOpenMetadataClient, OpenMetadataClient>().AddSecureOutboundHandlers(EgressIntegrations.OpenMetadata);
    }

    private sealed class OpenMetadataIntegrationMarker
    {
    }

    private sealed class OpenMetadataHttpClientMarker
    {
    }
}
