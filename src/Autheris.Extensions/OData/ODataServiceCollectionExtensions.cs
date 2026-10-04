namespace Autheris.Extensions.OData;

using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class ODataServiceCollectionExtensions
{
    /// <summary>OData v4 adapter (Power BI / Excel direct query).</summary>
    public static IServiceCollection AddODataIntegration(this IServiceCollection services, GatewayOptions gatewayOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(gatewayOptions);

        if (!ExtensionRegistration.TryBegin<ODataIntegrationMarker>(services))
        {
            return services;
        }

        services.TryAddScoped<IODataHandler, ODataHandler>();
        return services;
    }

    private sealed class ODataIntegrationMarker
    {
    }
}
