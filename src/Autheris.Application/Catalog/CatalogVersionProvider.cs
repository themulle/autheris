namespace Autheris.Application.Catalog;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;

/// <summary>
/// Provides typed and formatted catalog version stamps directly from the CATALOG_STATE table via the repository.
/// Single query reading (structure_version, content_version).
/// </summary>
public sealed class CatalogVersionProvider(ITableMetadataRepository metadataRepository) : ICatalogVersionProvider
{
    private readonly ITableMetadataRepository _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));

    public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) =>
        new(_metadataRepository.GetCatalogVersionAsync(ct));

    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        var version = await GetCatalogVersionAsync(ct).ConfigureAwait(false);
        return version.Formatted;
    }
}
