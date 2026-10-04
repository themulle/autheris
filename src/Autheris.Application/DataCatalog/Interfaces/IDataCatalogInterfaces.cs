namespace Autheris.Application.DataCatalog.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Models;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface IDataCatalogClient
{
    DataCatalogProviderType ProviderType { get; }
    Task<IReadOnlyList<CatalogTableAsset>> GetTablesAsync(string? filter = null, CancellationToken ct = default);
    Task<CatalogTableAsset?> GetTableAsync(TableIdentifier table, CancellationToken ct = default);
}

public interface IDataCatalogSyncService
{
    Task<CatalogSyncResult> SyncCatalogAsync(bool dryRun = false, CancellationToken ct = default);
    Task<TableMetadata?> EnrichOrReferenceTableAsync(TableIdentifier table, CancellationToken ct = default);
}
