namespace Autheris.Extensions.DataCatalog;

using System;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Autheris.Application.Interfaces;
using Autheris.Domain.Interfaces;

public sealed class DataCatalogClientFactory : IDataCatalogClientFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptions<GatewayOptions> _options;

    public DataCatalogClientFactory(
        IServiceProvider serviceProvider,
        IOptions<GatewayOptions> options)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public IDataCatalogClient CreateClient(DataCatalogProviderType providerType)
    {
        return providerType switch
        {
            DataCatalogProviderType.MicrosoftPurview => _serviceProvider.GetRequiredService<PurviewDataCatalogClient>(),
            DataCatalogProviderType.Collibra => _serviceProvider.GetRequiredService<CollibraDataCatalogClient>(),
            DataCatalogProviderType.OpenMetadata => _serviceProvider.GetRequiredService<OpenMetadataCatalogAdapter>(),
            DataCatalogProviderType.Alation => _serviceProvider.GetRequiredService<AlationCatalogClient>(),
            _ => throw new NotSupportedException($"Data catalog provider '{providerType}' is not supported.")
        };
    }

    public IDataCatalogClient GetActiveClient()
    {
        var configuredProvider = _options.Value.Catalog.Provider;
        return CreateClient(configuredProvider);
    }
}
