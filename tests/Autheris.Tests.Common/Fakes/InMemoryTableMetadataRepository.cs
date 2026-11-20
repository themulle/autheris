using Autheris.Application.Catalog;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Security;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// Passive in-memory table catalog. Seed with <see cref="Add"/>; reads return only seeded active tables
/// (unknown or inactive identifiers resolve to null, like the SQL repository). The repository has no tenant concept
/// (callers apply tenant visibility), so none is simulated. Writes upsert by identifier; every other operation
/// without an interface default (settings, discovery artifacts, key definitions) throws.
/// </summary>
public sealed class InMemoryTableMetadataRepository : ITableMetadataRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<TableIdentifier, TableMetadata> _tables = [];
    private readonly List<TableMetadata> _upserted = [];
    private readonly List<TableIdentifier> _getCalls = [];
    private int _upsertCalls;

    public int UpsertCalls => Volatile.Read(ref _upsertCalls);

    public IReadOnlyList<TableMetadata> UpsertedTables
    {
        get { lock (_gate) { return _upserted.ToArray(); } }
    }

    public IReadOnlyList<TableIdentifier> GetCalls
    {
        get { lock (_gate) { return _getCalls.ToArray(); } }
    }

    public Action<TableMetadata>? OnUpsert { get; set; }
    public bool SimulateEmptyBatchResult { get; set; }

    public InMemoryTableMetadataRepository(params TableMetadata[] seed)
    {
        foreach (var t in seed) { Add(t); }
    }

    public InMemoryTableMetadataRepository Add(TableMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        lock (_gate) { _tables[metadata.Identifier] = metadata; }
        return this;
    }

    public TableMetadata? Peek(TableIdentifier table)
    {
        lock (_gate) { return _tables.TryGetValue(table, out var m) ? m : null; }
    }

    public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _getCalls.Add(table);
            return Task.FromResult(_tables.TryGetValue(table, out var m) && m.Table.IsActive ? m : null);
        }
    }

    public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TableMetadata>>(_tables.Values.Where(m => m.Table.IsActive).ToList());
        }
    }

    public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
    {
        OnUpsert?.Invoke(metadata);
        Interlocked.Increment(ref _upsertCalls);
        lock (_gate)
        {
            _tables[metadata.Identifier] = metadata;
            _upserted.Add(metadata);
        }

        return Task.FromResult(metadata);
    }

    public async Task<IReadOnlyList<CatalogTableUpsertResult>> UpsertTableMetadataBatchAsync(
        IReadOnlyList<TableMetadata> batch,
        CancellationToken ct = default)
    {
        if (SimulateEmptyBatchResult)
        {
            return Array.Empty<CatalogTableUpsertResult>();
        }

        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return Array.Empty<CatalogTableUpsertResult>();
        }

        var results = new CatalogTableUpsertResult[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            var stored = await UpsertTableMetadataAsync(batch[i], ct).ConfigureAwait(false);
            results[i] = new CatalogTableUpsertResult(stored, CatalogUpsertKind.Updated, CatalogWriteClassification.Restrictive);
        }

        return results;
    }

    public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
        TableIdentifier table,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndexDefinition> indexes,
        IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
        CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(InMemoryTableMetadataRepository)} does not simulate key definition changes.");
}
