namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Model;

internal sealed class NullTableMetadataRepository : ITableMetadataRepository
{
    public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult<TableMetadata?>(null);
    public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>(Array.Empty<TableMetadata>());
    public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
    public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) =>
        Task.FromResult<TableKeyDefinitionChange?>(null);
}

internal sealed class NullRebacStore : IRebacStore
{
    public ValueTask AddTupleAsync(RebacTuple tuple, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask AddTuplesAsync(IEnumerable<RebacTuple> tuples, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<bool> DeleteTupleAsync(RebacTuple tuple, CancellationToken ct = default) => ValueTask.FromResult(true);
    public ValueTask<IReadOnlyList<RebacTuple>> GetTuplesAsync(string tenantId, string? user = null, string? relation = null, string? obj = null, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<RebacTuple>>(Array.Empty<RebacTuple>());
    public ValueTask ClearTenantTuplesAsync(string tenantId, CancellationToken ct = default) => ValueTask.CompletedTask;
}
