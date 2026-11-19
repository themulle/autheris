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

    public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
    {
        lock (_gate)
        {
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
        Add(metadata);
        return Task.FromResult(metadata);
    }

    public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
        TableIdentifier table,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndexDefinition> indexes,
        IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
        CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(InMemoryTableMetadataRepository)} does not simulate key definition changes.");
}
