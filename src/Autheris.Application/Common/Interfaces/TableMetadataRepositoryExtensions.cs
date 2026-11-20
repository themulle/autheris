namespace Autheris.Application.Common.Interfaces;

/// <summary>
/// Batch upsert and insert-if-absent helpers on <see cref="ITableMetadataRepository"/>.
/// </summary>
public static partial class TableMetadataRepositoryExtensions
{
    /// <summary>
    /// Calls <see cref="ITableMetadataRepository.InsertTableMetadataIfAbsentAsync"/> and falls back to
    /// <see cref="ITableMetadataRepository.GetTableMetadataAsync"/> + <see cref="ITableMetadataRepository.UpsertTableMetadataAsync"/>
    /// only when an unconfigured mock returns <see langword="null"/>.
    /// </summary>
    public static async Task<Autheris.Application.Catalog.CatalogTableUpsertResult> InsertTableMetadataIfAbsentWithFallbackAsync(
        this ITableMetadataRepository repository,
        TableMetadata metadata,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(metadata);

        var result = await repository.InsertTableMetadataIfAbsentAsync(metadata, ct).ConfigureAwait(false);
        if (result is not null)
        {
            return result;
        }

        var existing = await repository.GetTableMetadataAsync(metadata.Identifier, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return new Autheris.Application.Catalog.CatalogTableUpsertResult(
                existing,
                Autheris.Application.Catalog.CatalogUpsertKind.Unchanged,
                Autheris.Application.Catalog.CatalogWriteClassification.Unchanged);
        }

        var stored = await repository.UpsertTableMetadataAsync(metadata, ct).ConfigureAwait(false);
        return new Autheris.Application.Catalog.CatalogTableUpsertResult(
            stored,
            Autheris.Application.Catalog.CatalogUpsertKind.Created,
            Autheris.Application.Catalog.CatalogWriteClassification.Additive);
    }
}
