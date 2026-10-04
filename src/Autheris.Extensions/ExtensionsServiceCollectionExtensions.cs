namespace Autheris.Extensions;

using System;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Autheris.Extensions.Backstage;
using Autheris.Extensions.Cdc;
using Autheris.Extensions.DataCatalog;
using Autheris.Extensions.Dbt;
using Autheris.Extensions.Itsm;
using Autheris.Extensions.Lakehouse;
using Autheris.Extensions.Lineage;
using Autheris.Extensions.OData;
using Autheris.Extensions.OpenMetadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Single entry point used by the gateway core (<c>AddGatewayInfrastructure</c>) to register all connectors to
/// foreign systems. Every connector lives in its own folder and exposes its own <c>Add*Integration</c> method;
/// all methods are idempotent and only depend on core services resolved lazily via DI (options, secret provider,
/// repositories, SQL connection factory, CDC channel, lineage graph store), so the call order relative to the
/// core registrations does not matter.
/// </summary>
public static class ExtensionsServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayExtensions(
        this IServiceCollection services,
        GatewayOptions gatewayOptions,
        IHostEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(gatewayOptions);

        // SEC HIGH-03 / EX-12: shared SSRF handler for all outbound HttpClients of the extensions.
        services.TryAddTransient<SsrfProtectionHandler>();

        services.AddItsmIntegration(gatewayOptions);
        services.AddOpenMetadataIntegration(gatewayOptions);
        services.AddDataCatalogIntegration(gatewayOptions);
        services.AddDbtIntegration(gatewayOptions);
        services.AddODataIntegration(gatewayOptions);
        services.AddLakehouseIntegration(gatewayOptions);
        services.AddLineageExportIntegration(gatewayOptions);
        services.AddBackstageIntegration(gatewayOptions);
        services.AddCdcSourceIntegration(gatewayOptions);

        return services;
    }
}
