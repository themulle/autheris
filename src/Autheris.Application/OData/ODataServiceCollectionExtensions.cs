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
        services.TryAddScoped<IODataQueryPolicyEnforcer, ODataQueryPolicyEnforcer>();
        services.TryAddScoped<IODataHandler>(sp => new ODataHandler(
            sp.GetRequiredService<Autheris.Application.Common.Interfaces.ITableMetadataRepository>(),
            sp.GetRequiredService<Autheris.Application.Common.Interfaces.IGatewayExecutionService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ODataHandler>>(),
            sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>(),
            sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
            sp.GetRequiredService<Autheris.Application.Catalog.ICatalogVersionProvider>(),
            sp.GetRequiredService<Autheris.Application.Tenancy.ITenantResolver>(),
            sp.GetRequiredService<Autheris.Application.DataSources.IDataSourceOptionsResolver>(),
            sp.GetRequiredService<Autheris.Application.DataSources.IIngressTransportContext>(),
            sp.GetRequiredService<IODataQueryPolicyEnforcer>())
        {
            EpochProvider = sp.GetService<Autheris.Application.Governance.Caching.GovernanceEpochProvider>(),
            ScopeGuard = sp.GetService<Autheris.Application.Common.Quota.IScopeQueryExecutionGuard>()
        });
        return services;
    }

    private sealed class ODataIntegrationMarker
    {
    }
}
