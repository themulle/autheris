using System.Collections.Frozen;
using System.Collections.Immutable;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Catalog;

/// <summary>
/// Immutable, thread-safe snapshot of the catalog state (tables and relations) at a specific catalog version.
/// Supports dense ordinals for bitset filtering and O(1) indexed reverse relationship lookup.
/// </summary>
public sealed class CatalogSnapshot
{
    public CatalogVersion Version { get; }
    public DateTimeOffset CapturedAt { get; }
    public IReadOnlyList<TableMetadata> Tables { get; }
    public IReadOnlyDictionary<CatalogEntryKey, int> OrdinalByKey { get; }
    public IReadOnlyDictionary<Guid, int> OrdinalById { get; }
    public IReadOnlyDictionary<TableIdentifier, int> OrdinalByIdentifier { get; }
    public IReadOnlyList<TableRelation> Relations { get; }
    public IReadOnlyDictionary<Guid, IReadOnlyList<TableRelation>> RelationsByParentTableId { get; }
    public IReadOnlyDictionary<Guid, IReadOnlyList<TableRelation>> RelationsByChildTableId { get; }
    public int DegradedEntityCount { get; }

    public int TableCount => Tables.Count;

    public CatalogSnapshot(
        CatalogVersion version,
        DateTimeOffset capturedAt,
        IReadOnlyList<TableMetadata> tables,
        IReadOnlyDictionary<CatalogEntryKey, int> ordinalByKey,
        IReadOnlyDictionary<Guid, int> ordinalById,
        IReadOnlyDictionary<TableIdentifier, int> ordinalByIdentifier,
        IReadOnlyList<TableRelation> relations,
        IReadOnlyDictionary<Guid, IReadOnlyList<TableRelation>> relationsByParentTableId,
        IReadOnlyDictionary<Guid, IReadOnlyList<TableRelation>> relationsByChildTableId,
        int degradedEntityCount = 0)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(ordinalByKey);
        ArgumentNullException.ThrowIfNull(ordinalById);
        ArgumentNullException.ThrowIfNull(ordinalByIdentifier);
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(relationsByParentTableId);
        ArgumentNullException.ThrowIfNull(relationsByChildTableId);

        Version = version;
        CapturedAt = capturedAt;
        Tables = ToImmutable(tables);
        OrdinalByKey = ToFrozen(ordinalByKey);
        OrdinalById = ToFrozen(ordinalById);
        OrdinalByIdentifier = ToFrozen(ordinalByIdentifier);
        Relations = ToImmutable(relations);
        RelationsByParentTableId = FreezeRelationMap(relationsByParentTableId);
        RelationsByChildTableId = FreezeRelationMap(relationsByChildTableId);
        DegradedEntityCount = degradedEntityCount;
    }

    public static CatalogSnapshot Create(
        CatalogVersion version,
        IReadOnlyList<TableMetadata> tables,
        IReadOnlyList<TableRelation> relations,
        int degradedEntityCount = 0,
        DateTimeOffset? capturedAt = null)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(relations);

        var immutableTables = ToImmutable(tables);
        var immutableRelations = ToImmutable(relations);
        var timestamp = capturedAt ?? DateTimeOffset.UtcNow;
        var keyOrdinals = new Dictionary<CatalogEntryKey, int>(immutableTables.Length);
        var idOrdinals = new Dictionary<Guid, int>(immutableTables.Length);
        var identOrdinals = new Dictionary<TableIdentifier, int>(immutableTables.Length);

        for (int i = 0; i < immutableTables.Length; i++)
        {
            var table = immutableTables[i];
            var key = CatalogEntryKey.ForTable(table.Identifier);
            keyOrdinals.TryAdd(key, i);
            if (table.Table.Id != Guid.Empty)
            {
                idOrdinals.TryAdd(table.Table.Id, i);
                keyOrdinals.TryAdd(CatalogEntryKey.ForTable(table.Table.Id), i);
            }
            identOrdinals.TryAdd(table.Identifier, i);
        }

        var parentGroup = new Dictionary<Guid, List<TableRelation>>();
        var childGroup = new Dictionary<Guid, List<TableRelation>>();

        foreach (var rel in immutableRelations)
        {
            if (!parentGroup.TryGetValue(rel.ParentTableId, out var pList))
            {
                pList = new List<TableRelation>();
                parentGroup[rel.ParentTableId] = pList;
            }
            pList.Add(rel);

            if (!childGroup.TryGetValue(rel.ChildTableId, out var cList))
            {
                cList = new List<TableRelation>();
                childGroup[rel.ChildTableId] = cList;
            }
            cList.Add(rel);
        }

        var frozenParentGroup = parentGroup.ToFrozenDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<TableRelation>)kvp.Value.ToImmutableArray());
        var frozenChildGroup = childGroup.ToFrozenDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<TableRelation>)kvp.Value.ToImmutableArray());

        return new CatalogSnapshot(
            version,
            timestamp,
            immutableTables,
            keyOrdinals.ToFrozenDictionary(),
            idOrdinals.ToFrozenDictionary(),
            identOrdinals.ToFrozenDictionary(),
            immutableRelations,
            frozenParentGroup,
            frozenChildGroup,
            degradedEntityCount);
    }

    public static CatalogSnapshot Empty(CatalogVersion? version = null) =>
        Create(version ?? new CatalogVersion(1, 1), ImmutableArray<TableMetadata>.Empty, ImmutableArray<TableRelation>.Empty);

    /// <summary>
    /// Returns a new immutable <see cref="CatalogSnapshot"/> produced by removing or replacing
    /// <paramref name="changedIdentifiers"/> and inserting active <paramref name="updatedTables"/>.
    /// Existing snapshot collections and indices remain untouched for concurrent readers.
    /// </summary>
    public CatalogSnapshot WithDelta(
        CatalogVersion version,
        IReadOnlyCollection<TableIdentifier> changedIdentifiers,
        IReadOnlyList<TableMetadata> updatedTables,
        IReadOnlyList<TableRelation>? relations = null,
        int degradedEntityCount = 0,
        DateTimeOffset? capturedAt = null)
    {
        ArgumentNullException.ThrowIfNull(changedIdentifiers);
        ArgumentNullException.ThrowIfNull(updatedTables);

        var byIdentifier = new Dictionary<TableIdentifier, TableMetadata>(Tables.Count);
        for (var i = 0; i < Tables.Count; i++)
        {
            var existing = Tables[i];
            byIdentifier[existing.Identifier] = existing;
        }

        foreach (var changedId in changedIdentifiers)
        {
            byIdentifier.Remove(changedId);
        }

        for (var i = 0; i < updatedTables.Count; i++)
        {
            var updated = updatedTables[i];
            if (updated.Table.IsActive)
            {
                byIdentifier[updated.Identifier] = updated;
            }
            else
            {
                byIdentifier.Remove(updated.Identifier);
            }
        }

        return Create(
            version,
            byIdentifier.Values.ToList(),
            relations ?? Relations,
            degradedEntityCount,
            capturedAt);
    }

    public bool TryGetOrdinal(CatalogEntryKey key, out int ordinal) =>
        OrdinalByKey.TryGetValue(key, out ordinal);

    public bool TryGetOrdinal(Guid tableId, out int ordinal) =>
        OrdinalById.TryGetValue(tableId, out ordinal);

    public bool TryGetOrdinal(TableIdentifier identifier, out int ordinal) =>
        OrdinalByIdentifier.TryGetValue(identifier, out ordinal);

    public TableMetadata? GetTableByOrdinal(int ordinal)
    {
        if ((uint)ordinal < (uint)Tables.Count)
        {
            return Tables[ordinal];
        }
        return null;
    }

    public TableMetadata? FindTable(CatalogEntryKey key) =>
        TryGetOrdinal(key, out var ord) ? Tables[ord] : null;

    public TableMetadata? FindTable(Guid tableId) =>
        TryGetOrdinal(tableId, out var ord) ? Tables[ord] : null;

    public TableMetadata? FindTable(TableIdentifier identifier) =>
        TryGetOrdinal(identifier, out var ord) ? Tables[ord] : null;

    public IReadOnlyList<TableRelation> GetParentRelations(Guid parentTableId) =>
        RelationsByParentTableId.TryGetValue(parentTableId, out var rels) ? rels : ImmutableArray<TableRelation>.Empty;

    public IReadOnlyList<TableRelation> GetChildRelations(Guid childTableId) =>
        RelationsByChildTableId.TryGetValue(childTableId, out var rels) ? rels : ImmutableArray<TableRelation>.Empty;

    private static ImmutableArray<T> ToImmutable<T>(IReadOnlyList<T> source) =>
        source is ImmutableArray<T> imm ? imm : ImmutableArray.CreateRange(source);

    private static IReadOnlyDictionary<TKey, TValue> ToFrozen<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> source)
        where TKey : notnull =>
        source is FrozenDictionary<TKey, TValue> frozen ? frozen : source.ToFrozenDictionary();

    private static IReadOnlyDictionary<Guid, IReadOnlyList<TableRelation>> FreezeRelationMap(
        IReadOnlyDictionary<Guid, IReadOnlyList<TableRelation>> source)
    {
        if (source is FrozenDictionary<Guid, IReadOnlyList<TableRelation>> frozen &&
            frozen.Values.All(static v => v is ImmutableArray<TableRelation>))
        {
            return frozen;
        }

        return source.ToFrozenDictionary(
            static kvp => kvp.Key,
            static kvp => (IReadOnlyList<TableRelation>)ToImmutable(kvp.Value));
    }
}
