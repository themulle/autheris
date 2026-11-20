using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Models;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// Configurable in-memory <see cref="IDataCatalogClient"/> for DataCatalog sync unit tests.
/// </summary>
public sealed class StubDataCatalogClient(DataCatalogProviderType providerType = DataCatalogProviderType.OpenMetadata) : IDataCatalogClient
{
    public DataCatalogProviderType ProviderType { get; set; } = providerType;
    public IReadOnlyList<CatalogTableAsset> Tables { get; set; } = [];
    public Func<Task<IReadOnlyList<CatalogTableAsset>>>? OnGetTables { get; set; }

    public Task<IReadOnlyList<CatalogTableAsset>> GetTablesAsync(string? filter = null, CancellationToken ct = default)
        => OnGetTables is not null ? OnGetTables() : Task.FromResult(Tables);

    public Task<CatalogTableAsset?> GetTableAsync(TableIdentifier table, CancellationToken ct = default)
        => Task.FromResult(Tables.FirstOrDefault(t => t.Identifier.Equals(table)));
}
