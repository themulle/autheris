namespace Autheris.Modules.OpenMetadata;

using System;
using System.Collections.Generic;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Autheris.Modules.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Autheris module for OpenMetadata governance sync, webhook event ingestion, and catalog adapter registration.
/// </summary>
public sealed class OpenMetadataModule : IAutherisModule
{
    private static readonly string[] OwnedConfigurationSections = [GatewaySections.OpenMetadata];

    public string Id => "openmetadata";

    public IReadOnlyList<string> ConfigurationSections => OwnedConfigurationSections;

    public void ConfigureServices(
        IServiceCollection services,
        IConfiguration moduleConfiguration,
        IModuleHostContext host)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient<IOpenMetadataClient, OpenMetadataClient>()
            .AddSecureOutboundHandlers(EgressIntegrations.OpenMetadata);

        services.TryAddScoped<IOpenMetadataSyncService, OpenMetadataSyncService>();
        services.TryAddScoped<IOpenMetadataWebhookHandler, OpenMetadataSyncService>();
        services.TryAddTransient<IDataCatalogClient, OpenMetadataCatalogAdapter>();
    }

    public void MapEndpoints(IModuleEndpointRegistrar endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
    }
}
