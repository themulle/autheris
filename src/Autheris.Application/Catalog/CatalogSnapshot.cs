using System.Collections.Frozen;
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
        Version = version;
        CapturedAt = capturedAt;
        Tables = tables;
        OrdinalByKey = ordinalByKey;
        OrdinalById = ordinalById;
        OrdinalByIdentifier = ordinalByIdentifier;
        Relations = relations;
        RelationsByParentTableId = relationsByParentTableId;
        RelationsByChildTableId = relationsByChildTableId;
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

        var timestamp = capturedAt ?? DateTimeOffset.UtcNow;
        var keyOrdinals = new Dictionary<CatalogEntryKey, int>(tables.Count);
        var idOrdinals = new Dictionary<Guid, int>(tables.Count);
        var identOrdinals = new Dictionary<TableIdentifier, int>(tables.Count);

        for (int i = 0; i < tables.Count; i++)
        {
            var table = tables[i];
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

        foreach (var rel in relations)
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
            kvp => (IReadOnlyList<TableRelation>)kvp.Value.AsReadOnly());
        var frozenChildGroup = childGroup.ToFrozenDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<TableRelation>)kvp.Value.AsReadOnly());

        return new CatalogSnapshot(
            version,
            timestamp,
            tables,
            keyOrdinals.ToFrozenDictionary(),
            idOrdinals.ToFrozenDictionary(),
            identOrdinals.ToFrozenDictionary(),
            relations,
            frozenParentGroup,
            frozenChildGroup,
            degradedEntityCount);
    }

    public static CatalogSnapshot Empty(CatalogVersion? version = null) =>
        Create(version ?? new CatalogVersion(1, 1), Array.Empty<TableMetadata>(), Array.Empty<TableRelation>());

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
        RelationsByParentTableId.TryGetValue(parentTableId, out var rels) ? rels : Array.Empty<TableRelation>();

    public IReadOnlyList<TableRelation> GetChildRelations(Guid childTableId) =>
        RelationsByChildTableId.TryGetValue(childTableId, out var rels) ? rels : Array.Empty<TableRelation>();
}
