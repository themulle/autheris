using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
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

    private CatalogSnapshot(
        CatalogVersion version,
        DateTimeOffset capturedAt,
        ImmutableArray<TableMetadata> tables,
        IReadOnlyDictionary<CatalogEntryKey, int> ordinalByKey,
        IReadOnlyDictionary<Guid, int> ordinalById,
        IReadOnlyDictionary<TableIdentifier, int> ordinalByIdentifier,
        ImmutableArray<TableRelation> relations,
        IReadOnlyDictionary<Guid, IReadOnlyList<TableRelation>> relationsByParentTableId,
        IReadOnlyDictionary<Guid, IReadOnlyList<TableRelation>> relationsByChildTableId,
        int degradedEntityCount)
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

        var immutableTables = ToImmutable(tables);
        var immutableRelations = ToImmutable(relations);
        var timestamp = ResolveCapturedAt(capturedAt);
        var keyOrdinals = ImmutableDictionary.CreateBuilder<CatalogEntryKey, int>();
        var idOrdinals = ImmutableDictionary.CreateBuilder<Guid, int>();
        var identOrdinals = ImmutableDictionary.CreateBuilder<TableIdentifier, int>();

        for (int i = 0; i < immutableTables.Length; i++)
        {
            SetOrdinals(immutableTables[i], i, keyOrdinals, idOrdinals, identOrdinals);
        }

        var (frozenParentGroup, frozenChildGroup) = BuildRelationMaps(immutableRelations);

        return new CatalogSnapshot(
            version,
            timestamp,
            immutableTables,
            keyOrdinals.ToImmutable(),
            idOrdinals.ToImmutable(),
            identOrdinals.ToImmutable(),
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

        var timestamp = ResolveCapturedAt(capturedAt);
        var tablesList = new List<TableMetadata>(Tables.Count + updatedTables.Count);
        tablesList.AddRange(Tables);

        var keyBuilder = ToImmutableBuilder(OrdinalByKey);
        var idBuilder = ToImmutableBuilder(OrdinalById);
        var identBuilder = ToImmutableBuilder(OrdinalByIdentifier);

        var activeUpdates = new Dictionary<TableIdentifier, TableMetadata>(updatedTables.Count);
        HashSet<TableIdentifier>? inactiveUpdates = null;
        for (var i = 0; i < updatedTables.Count; i++)
        {
            var updated = updatedTables[i];
            if (updated.Table.IsActive)
            {
                activeUpdates[updated.Identifier] = updated;
                inactiveUpdates?.Remove(updated.Identifier);
            }
            else
            {
                activeUpdates.Remove(updated.Identifier);
                (inactiveUpdates ??= new HashSet<TableIdentifier>()).Add(updated.Identifier);
            }
        }

        foreach (var changedId in changedIdentifiers)
        {
            if (!activeUpdates.ContainsKey(changedId))
            {
                RemoveAtIdentifier(changedId, tablesList, keyBuilder, idBuilder, identBuilder);
            }
        }

        if (inactiveUpdates != null)
        {
            foreach (var inactiveId in inactiveUpdates)
            {
                RemoveAtIdentifier(inactiveId, tablesList, keyBuilder, idBuilder, identBuilder);
            }
        }

        foreach (var (id, updated) in activeUpdates)
        {
            if (identBuilder.TryGetValue(id, out var existingOrd))
            {
                var previous = tablesList[existingOrd];
                tablesList[existingOrd] = updated;
                if (previous.Table.Id != updated.Table.Id)
                {
                    if (previous.Table.Id != Guid.Empty)
                    {
                        RemoveIfMappedTo(idBuilder, previous.Table.Id, existingOrd);
                        RemoveIfMappedTo(keyBuilder, CatalogEntryKey.ForTable(previous.Table.Id), existingOrd);
                    }

                    if (updated.Table.Id != Guid.Empty)
                    {
                        idBuilder[updated.Table.Id] = existingOrd;
                        keyBuilder[CatalogEntryKey.ForTable(updated.Table.Id)] = existingOrd;
                    }
                }
            }
            else
            {
                var newOrd = tablesList.Count;
                tablesList.Add(updated);
                SetOrdinals(updated, newOrd, keyBuilder, idBuilder, identBuilder);
            }
        }

        ImmutableArray<TableRelation> nextRelations;
        IReadOnlyDictionary<Guid, IReadOnlyList<TableRelation>> nextParentRelations;
        IReadOnlyDictionary<Guid, IReadOnlyList<TableRelation>> nextChildRelations;
        if (relations is null)
        {
            nextRelations = ToImmutable(Relations);
            nextParentRelations = RelationsByParentTableId;
            nextChildRelations = RelationsByChildTableId;
        }
        else
        {
            nextRelations = ToImmutable(relations);
            (nextParentRelations, nextChildRelations) = BuildRelationMaps(nextRelations);
        }

        return new CatalogSnapshot(
            version,
            timestamp,
            ImmutableCollectionsMarshal.AsImmutableArray(tablesList.ToArray()),
            keyBuilder.ToImmutable(),
            idBuilder.ToImmutable(),
            identBuilder.ToImmutable(),
            nextRelations,
            nextParentRelations,
            nextChildRelations,
            degradedEntityCount);
    }

    private static void SetOrdinals(
        TableMetadata table,
        int ordinal,
        ImmutableDictionary<CatalogEntryKey, int>.Builder keyOrdinals,
        ImmutableDictionary<Guid, int>.Builder idOrdinals,
        ImmutableDictionary<TableIdentifier, int>.Builder identOrdinals)
    {
        keyOrdinals[CatalogEntryKey.ForTable(table.Identifier)] = ordinal;
        if (table.Table.Id != Guid.Empty)
        {
            idOrdinals[table.Table.Id] = ordinal;
            keyOrdinals[CatalogEntryKey.ForTable(table.Table.Id)] = ordinal;
        }

        identOrdinals[table.Identifier] = ordinal;
    }

    private static void RemoveAtIdentifier(
        TableIdentifier identifier,
        List<TableMetadata> tablesList,
        ImmutableDictionary<CatalogEntryKey, int>.Builder keyBuilder,
        ImmutableDictionary<Guid, int>.Builder idBuilder,
        ImmutableDictionary<TableIdentifier, int>.Builder identBuilder)
    {
        if (!identBuilder.TryGetValue(identifier, out var ord))
        {
            return;
        }

        var removed = tablesList[ord];
        identBuilder.Remove(identifier);
        RemoveIfMappedTo(keyBuilder, CatalogEntryKey.ForTable(identifier), ord);
        if (removed.Table.Id != Guid.Empty)
        {
            RemoveIfMappedTo(idBuilder, removed.Table.Id, ord);
            RemoveIfMappedTo(keyBuilder, CatalogEntryKey.ForTable(removed.Table.Id), ord);
        }

        var lastIdx = tablesList.Count - 1;
        if (ord != lastIdx)
        {
            var lastTable = tablesList[lastIdx];
            tablesList[ord] = lastTable;
            SetOrdinals(lastTable, ord, keyBuilder, idBuilder, identBuilder);
        }

        tablesList.RemoveAt(lastIdx);
    }

    private static void RemoveIfMappedTo<TKey>(
        ImmutableDictionary<TKey, int>.Builder builder,
        TKey key,
        int expectedOrdinal)
        where TKey : notnull
    {
        if (builder.TryGetValue(key, out var current) && current == expectedOrdinal)
        {
            builder.Remove(key);
        }
    }

    private static ImmutableDictionary<TKey, int>.Builder ToImmutableBuilder<TKey>(IReadOnlyDictionary<TKey, int> source)
        where TKey : notnull
    {
        if (source is ImmutableDictionary<TKey, int> imm)
        {
            return imm.ToBuilder();
        }

        var builder = ImmutableDictionary.CreateBuilder<TKey, int>();
        foreach (var kvp in source)
        {
            builder[kvp.Key] = kvp.Value;
        }

        return builder;
    }

    private static (IReadOnlyDictionary<Guid, IReadOnlyList<TableRelation>> Parent, IReadOnlyDictionary<Guid, IReadOnlyList<TableRelation>> Child)
        BuildRelationMaps(ImmutableArray<TableRelation> immutableRelations)
    {
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

        return (frozenParentGroup, frozenChildGroup);
    }

    private static DateTimeOffset ResolveCapturedAt(DateTimeOffset? capturedAt) =>
        capturedAt ?? DateTimeOffset.UtcNow;

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
