namespace Autheris.Application.DataCatalog.Interfaces;

using Autheris.Domain.Common;

public interface IDataCatalogClientFactory
{
    IDataCatalogClient CreateClient(DataCatalogProviderType providerType);
    IDataCatalogClient GetActiveClient();
}
