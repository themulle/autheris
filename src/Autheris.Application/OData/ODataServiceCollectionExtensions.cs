namespace Autheris.Application.OData;

using System;
using System.Linq;
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

        if (services.Any(d => d.ServiceType == typeof(ODataIntegrationMarker)))
        {
            return services;
        }

        services.AddSingleton(new ODataIntegrationMarker());
        services.TryAddScoped<IODataHandler, ODataHandler>();
        return services;
    }

    private sealed class ODataIntegrationMarker
    {
    }
}
