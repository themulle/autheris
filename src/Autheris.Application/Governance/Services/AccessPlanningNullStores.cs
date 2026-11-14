namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;

internal sealed class NullTableMetadataRepository : ITableMetadataRepository
{
    public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult<TableMetadata?>(null);
    public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>(Array.Empty<TableMetadata>());
    public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);

    public Task<IReadOnlyList<Autheris.Application.Catalog.CatalogTableUpsertResult>> UpsertTableMetadataBatchAsync(
        IReadOnlyList<TableMetadata> batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<Autheris.Application.Catalog.CatalogTableUpsertResult>>(
                Array.Empty<Autheris.Application.Catalog.CatalogTableUpsertResult>());
        }

        IReadOnlyList<Autheris.Application.Catalog.CatalogTableUpsertResult> results = batch
            .Select(item => new Autheris.Application.Catalog.CatalogTableUpsertResult(
                item,
                Autheris.Application.Catalog.CatalogUpsertKind.Updated,
                Autheris.Application.Catalog.CatalogWriteClassification.Restrictive))
            .ToArray();
        return Task.FromResult(results);
    }
    public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) =>
        Task.FromResult<TableKeyDefinitionChange?>(null);
}
