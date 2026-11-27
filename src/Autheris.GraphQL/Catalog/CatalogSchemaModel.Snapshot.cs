using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Analytics;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql.Tree.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

namespace Autheris.GraphQL.Catalog;

public sealed partial class CatalogSchemaModel
{
    private static readonly HashSet<string> ReservedFilterKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "not", "where", "orderBy"
    };

    internal sealed record TableBuilder(
        TableMetadata Metadata,
        string TypeName,
        string QueryFieldName,
        string FilterTypeName,
        string OrderByTypeName,
        List<CatalogColumnField> Columns);

    /// <summary>
    /// Issue #1069: pure in-memory build from an immutable <see cref="CatalogSnapshot"/> with zero database round-trips.
    /// </summary>
    internal static CatalogSchemaModel BuildFromSnapshot(
        CatalogSnapshot snapshot,
        ILogger? logger = null,
        Autheris.Application.Sql.Dialects.ISqlDialectRegistry? dialectRegistry = null,
        bool crossSourceEnabled = false,
        Func<TableMetadata, bool>? tableFilter = null,
        TimeProvider? timeProvider = null,
        string phase = "snapshot")
    {
        var (finalTables, omittedSnapshot) = BuildModelDataFromSnapshot(
            snapshot, logger, dialectRegistry, crossSourceEnabled, tableFilter, fingerprintCache: null, timeProvider, phase);
        return new CatalogSchemaModel(finalTables, omittedSnapshot);
    }

    internal static async ValueTask<CatalogSchemaModel> BuildFromSnapshotAsync(
        CatalogSnapshot snapshot,
        ILogger? logger = null,
        Autheris.Application.Sql.Dialects.ISqlDialectRegistry? dialectRegistry = null,
        bool crossSourceEnabled = false,
        Func<TableMetadata, bool>? tableFilter = null,
        CatalogTableFingerprintCache? fingerprintCache = null,
        IEpochValidationService? epochService = null,
        TimeProvider? timeProvider = null,
        string phase = "snapshot",
        CancellationToken ct = default)
    {
        var (finalTables, omittedSnapshot) = BuildModelDataFromSnapshot(
            snapshot, logger, dialectRegistry, crossSourceEnabled, tableFilter, fingerprintCache, timeProvider, phase);
        var precomputedFingerprint = await ComputeFingerprintIfCachedAsync(
            fingerprintCache, epochService, finalTables, omittedSnapshot, logger, ct).ConfigureAwait(false);
        return new CatalogSchemaModel(finalTables, omittedSnapshot, precomputedFingerprint);
    }

    private static (List<CatalogTableType> FinalTables, CatalogOmittedTable[] OmittedSnapshot) BuildModelDataFromSnapshot(
        CatalogSnapshot snapshot,
        ILogger? logger,
        Autheris.Application.Sql.Dialects.ISqlDialectRegistry? dialectRegistry,
        bool crossSourceEnabled,
        Func<TableMetadata, bool>? tableFilter,
        CatalogTableFingerprintCache? fingerprintCache,
        TimeProvider? timeProvider,
        string phase)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var registry = dialectRegistry ?? Autheris.Application.Sql.Dialects.ISqlDialectRegistry.Default;
        var effectiveTime = timeProvider ?? TimeProvider.System;
        long buildStartTimestamp = effectiveTime.GetTimestamp();
        long lastProgressTimestamp = buildStartTimestamp;

        var (omittedTables, tableBuilders, tableMapById, tablesProcessed) = PrepareTableBuilders(
            snapshot.Tables,
            registry,
            tableFilter,
            fingerprintCache,
            logger,
            effectiveTime,
            buildStartTimestamp,
            ref lastProgressTimestamp,
            phase);

        var relationsByTable = new Dictionary<TableIdentifier, List<TableRelation>>();
        foreach (var rel in snapshot.Relations)
        {
            if (!relationsByTable.TryGetValue(rel.ParentTableIdentifier, out var list))
            {
                list = [];
                relationsByTable[rel.ParentTableIdentifier] = list;
            }

            list.Add(rel);
        }

        return AssembleModelData(
            tableBuilders,
            tableMapById,
            relationsByTable,
            omittedTables,
            crossSourceEnabled,
            logger,
            effectiveTime,
            buildStartTimestamp,
            ref lastProgressTimestamp,
            tablesProcessed,
            phase);
    }

    private static (
        List<CatalogOmittedTable> OmittedTables,
        List<TableBuilder> TableBuilders,
        Dictionary<TableIdentifier, TableBuilder> TableMapById,
        int TablesProcessed) PrepareTableBuilders(
        IReadOnlyList<TableMetadata> allTables,
        Autheris.Application.Sql.Dialects.ISqlDialectRegistry registry,
        Func<TableMetadata, bool>? tableFilter,
        CatalogTableFingerprintCache? fingerprintCache,
        ILogger? logger,
        TimeProvider effectiveTime,
        long buildStartTimestamp,
        ref long lastProgressTimestamp,
        string phase)
    {
        int tablesProcessed = 0;

        // Filter: only active, discovery-visible SQL tables whose dialect the tree compiler supports.
        // Hidden tables are evicted from any fingerprint cache and excluded silently from the GraphQL schema.
        var sqlTables = new List<TableMetadata>();
        foreach (var candidate in allTables)
        {
            if (candidate.Table.DiscoveryHidden)
            {
                fingerprintCache?.Invalidate(candidate.Identifier);
                continue;
            }

            if (!candidate.Table.IsActive || candidate.DataSourceType != DataSourceType.Sql || candidate.Table.ObjectKind is DiscoveryObjectKind.MetricView or DiscoveryObjectKind.Procedure)
            {
                continue;
            }

            if (tableFilter != null && !tableFilter(candidate))
            {
                fingerprintCache?.Invalidate(candidate.Identifier);
                continue;
            }

            sqlTables.Add(candidate);
            tablesProcessed++;
            MaybeLogBuildProgress(logger, effectiveTime, buildStartTimestamp, ref lastProgressTimestamp, tablesProcessed, phase);
        }

        var omittedTables = new List<CatalogOmittedTable>();
        foreach (var excluded in sqlTables.Where(t =>
                     registry.TryGetByName(t.Table.SourceType, out var provider) &&
                     provider.Descriptor.IsExecutable &&
                     !provider.Descriptor.SupportsTreeCompile))
        {
            logger?.LogInformation(
                "GraphQL catalog schema: table '{Table}' (dialect '{Dialect}') is not exposed because the GraphQL tree compiler does not support its dialect; it stays available through OData, REST and MCP.",
                excluded.Identifier.ToQualifiedName(), excluded.Table.SourceType);
        }

        var supportedSqlTables = new List<TableMetadata>(sqlTables.Count);
        foreach (var t in sqlTables)
        {
            if (registry.TryGetByName(t.Table.SourceType, out var provider) &&
                provider.Descriptor.IsExecutable &&
                provider.Descriptor.SupportsTreeCompile)
            {
                supportedSqlTables.Add(t);
            }
            else
            {
                // BUG #157: only active SQL tables reach this point (inactive and non-SQL were dropped above).
                omittedTables.Add(new CatalogOmittedTable(t.Identifier, CatalogTableOmissionReason.UnsupportedDialect));
            }
        }

        var activeSqlTables = supportedSqlTables
            .OrderBy(t => t.Identifier.Domain, StringComparer.Ordinal)
            .ThenBy(t => t.Identifier.Schema, StringComparer.Ordinal)
            .ThenBy(t => t.Identifier.TableName, StringComparer.Ordinal)
            .ToList();

        var usedSchemaTypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Query", "Mutation", "Subscription", "AutherisSortDirection",
            "AutherisStringFilter", "AutherisIntFilter", "AutherisLongFilter",
            "AutherisFloatFilter", "AutherisDecimalFilter", "AutherisBooleanFilter",
            "AutherisDateTimeFilter"
        };
        foreach (var shared in AggregateTypeNames.SharedTypeNames)
        {
            usedSchemaTypeNames.Add(shared);
        }

        var tableBuilders = new List<TableBuilder>(activeSqlTables.Count);
        foreach (var meta in activeSqlTables)
        {
            var baseName = SanitizeGraphQlName($"{meta.Identifier.Domain}_{meta.Identifier.Schema}_{meta.Identifier.TableName}");
            var typeName = baseName;
            var filterTypeName = $"{typeName}_filter";
            var orderTypeName = $"{typeName}_order_by";

            var columns = new List<CatalogColumnField>();
            var usedColNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var col in meta.Columns ?? [])
            {
                if (col.DiscoveryHidden)
                {
                    continue;
                }

                // R-GQL-13: the tree compiler only accepts plain column names; others (e.g. "a-b") were offered in the
                // schema under a sanitized name but could never be queried.
                if (!SqlIdentifierGuard.IsValidIdentifier(col.ColumnName))
                {
                    logger?.LogInformation("GraphQL catalog schema: column '{Column}' of '{Table}' is not a plain identifier and is omitted.", col.ColumnName, meta.Identifier.ToQualifiedName());
                    continue;
                }

                var colFieldName = SanitizeGraphQlName(col.ColumnName);
                if (ReservedFilterKeywords.Contains(colFieldName))
                {
                    colFieldName = $"{colFieldName}_col";
                }

                var colSuffix = 2;
                while (!usedColNames.Add(colFieldName))
                {
                    colFieldName = $"{SanitizeGraphQlName(col.ColumnName)}_{colSuffix++}";
                }

                var fieldType = MapDataType(col.DataType);
                if (meta.ColumnMaskingRules != null &&
                    meta.ColumnMaskingRules.TryGetValue(col.ColumnName, out var maskRule) &&
                    maskRule.IsHmac)
                {
                    fieldType = CatalogFieldType.String;
                }

                columns.Add(new CatalogColumnField(colFieldName, col.ColumnName, fieldType, col.DataType, true, col.Description));
            }

            // BUG #157: a table without queryable columns would yield field-less GraphQL types and abort schema build. It is
            // omitted before the collision check and before any name is reserved.
            if (columns.Count == 0)
            {
                omittedTables.Add(new CatalogOmittedTable(meta.Identifier, CatalogTableOmissionReason.NoQueryableColumns));
                logger?.LogWarning(
                    "GraphQL catalog schema: table '{Table}' has no queryable columns and is omitted from the GraphQL schema.",
                    meta.Identifier.ToQualifiedName());
                continue;
            }

            string? collidingName = null;
            if (usedSchemaTypeNames.Contains(typeName)) collidingName = typeName;
            else if (usedSchemaTypeNames.Contains(filterTypeName)) collidingName = filterTypeName;
            else if (usedSchemaTypeNames.Contains(orderTypeName)) collidingName = orderTypeName;
            else
            {
                // #112 A1-11: the typed aggregate names (and the T_aggregate root field) are reserved per table. The table list is
                // sorted ordinally, so the omission is deterministic and independent of the caller (the schema is global).
                var aggregateNames = AggregateTypeNames.For(typeName);
                collidingName = aggregateNames.All().FirstOrDefault(usedSchemaTypeNames.Contains);
            }

            if (collidingName != null)
            {
                omittedTables.Add(new CatalogOmittedTable(meta.Identifier, CatalogTableOmissionReason.NameCollision));
                logger?.LogError(
                    "GraphQL catalog schema: table '{Table}' conflicts with existing schema type name '{CollidingName}'. The table is omitted from GraphQL schema generation to preserve schema stability.",
                    meta.Identifier.ToQualifiedName(),
                    collidingName);
                continue;
            }

            usedSchemaTypeNames.Add(typeName);
            usedSchemaTypeNames.Add(filterTypeName);
            usedSchemaTypeNames.Add(orderTypeName);
            foreach (var aggregateName in AggregateTypeNames.For(typeName).All())
            {
                usedSchemaTypeNames.Add(aggregateName);
            }

            tableBuilders.Add(new TableBuilder(meta, typeName, typeName, filterTypeName, orderTypeName, columns));
        }

        var tableMapById = tableBuilders.ToDictionary(t => t.Metadata.Identifier, t => t);
        return (omittedTables, tableBuilders, tableMapById, tablesProcessed);
    }

    private static (List<CatalogTableType> FinalTables, CatalogOmittedTable[] OmittedSnapshot) AssembleModelData(
        List<TableBuilder> tableBuilders,
        Dictionary<TableIdentifier, TableBuilder> tableMapById,
        Dictionary<TableIdentifier, List<TableRelation>> relationsByTable,
        List<CatalogOmittedTable> omittedTables,
        bool crossSourceEnabled,
        ILogger? logger,
        TimeProvider effectiveTime,
        long buildStartTimestamp,
        ref long lastProgressTimestamp,
        int tablesProcessed,
        string phase)
    {
        // Issue #1099: relation ids are random, so the order of the input (and of the stored rows) must not decide which
        // relation gets which field name. Sort every parent's relations by a stable, content-based key (O(n log n)).
        foreach (var parentRelations in relationsByTable.Values)
        {
            parentRelations.Sort(RelationNamingOrder.Instance);
        }

        // Reverse index (O(n)): child table -> (parent builder, relation), in tableBuilders order then relation order,
        // so reverse field names, suffixes and ordering are identical to scanning every table per table.
        var reverseByChild = new Dictionary<TableIdentifier, List<(int ParentIndex, TableRelation Relation)>>();
        for (var parentIndex = 0; parentIndex < tableBuilders.Count; parentIndex++)
        {
            if (!relationsByTable.TryGetValue(tableBuilders[parentIndex].Metadata.Identifier, out var parentRelations))
            {
                continue;
            }

            foreach (var rel in parentRelations)
            {
                if (!reverseByChild.TryGetValue(rel.ChildTableIdentifier, out var revList))
                {
                    revList = [];
                    reverseByChild[rel.ChildTableIdentifier] = revList;
                }

                revList.Add((parentIndex, rel));
            }
        }

        // Map relations. A relation between tables the tree service cannot run as one statement (different data source or
        // dialect) is not exposed: it would be advertised and then always fail. Omitted before any field name is allocated.
        var omittedRelations = 0;
        var invalidRelations = new HashSet<Guid>();
        var finalTables = new List<CatalogTableType>(tableBuilders.Count);

        foreach (var builder in tableBuilders)
        {
            var meta = builder.Metadata;
            var columns = builder.Columns;
            var relations = new List<CatalogRelationField>();
            var usedRelationFieldNames = new HashSet<string>(columns.Select(c => c.FieldName), StringComparer.Ordinal);
            var nextSuffixByBase = new Dictionary<string, int>(StringComparer.Ordinal);

            if (relationsByTable.TryGetValue(meta.Identifier, out var rawRelations))
            {
                var exposed = new List<(TableRelation Relation, TableBuilder Target, bool DefaultNamed)>(rawRelations.Count);
                Dictionary<string, int>? defaultNamedPerChild = null;
                foreach (var rel in rawRelations)
                {
                    if (!tableMapById.TryGetValue(rel.ChildTableIdentifier, out var targetTable))
                    {
                        continue;
                    }

                    if (!HasValidKeys(rel, meta, targetTable.Metadata))
                    {
                        invalidRelations.Add(rel.Id);
                        continue;
                    }

                    if (!RelationExecutability.IsExecutable(rel, meta, targetTable.Metadata, crossSourceEnabled))
                    {
                        omittedRelations++;
                        continue;
                    }

                    var defaultNamed = RelationNameDefaults.IsDefaultFor(rel.RelationName, meta.Identifier.TableName);
                    if (defaultNamed)
                    {
                        defaultNamedPerChild ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        var childName = rel.ChildTableIdentifier.TableName;
                        defaultNamedPerChild[childName] = defaultNamedPerChild.GetValueOrDefault(childName) + 1;
                    }

                    exposed.Add((rel, targetTable, defaultNamed));
                }

                foreach (var (rel, targetTable, defaultNamed) in exposed)
                {
                    var isList = rel.Cardinality == RelationCardinality.OneToMany;
                    var rawFieldName = defaultNamed
                        ? DefaultNamedRelationFieldName(rel, defaultNamedPerChild![rel.ChildTableIdentifier.TableName] > 1)
                        : rel.RelationName;
                    var relFieldName = AllocateRelationFieldName(rawFieldName, usedRelationFieldNames, nextSuffixByBase);

                    relations.Add(new CatalogRelationField(
                        relFieldName,
                        rel.RelationName,
                        rel.ChildTableIdentifier,
                        targetTable.TypeName,
                        isList,
                        rel.JoinKeysParent,
                        rel.JoinKeysChild,
                        rel.Cardinality));
                }
            }

            reverseByChild.TryGetValue(meta.Identifier, out var reverseRelations);
            foreach (var (parentIndex, rel) in (IEnumerable<(int ParentIndex, TableRelation Relation)>?)reverseRelations ?? [])
            {
                var other = tableBuilders[parentIndex];
                if (other.Metadata.Identifier.Equals(meta.Identifier)
                    || !HasValidKeys(rel, other.Metadata, meta)
                    || !RelationExecutability.IsExecutable(rel, other.Metadata, meta, crossSourceEnabled))
                {
                    continue;
                }

                var revCardinality = rel.Cardinality switch
                {
                    RelationCardinality.OneToMany => RelationCardinality.ManyToOne,
                    RelationCardinality.ManyToOne => RelationCardinality.OneToMany,
                    _ => RelationCardinality.OneToOne
                };
                var isList = revCardinality == RelationCardinality.OneToMany;
                var revFieldName = AllocateRelationFieldName(
                    $"{other.Metadata.Identifier.TableName}_by_{rel.RelationName}", usedRelationFieldNames, nextSuffixByBase);

                relations.Add(new CatalogRelationField(
                    revFieldName,
                    rel.RelationName,
                    other.Metadata.Identifier,
                    other.TypeName,
                    isList,
                    rel.JoinKeysChild,
                    rel.JoinKeysParent,
                    revCardinality,
                    IsReverse: true));
            }

            finalTables.Add(new CatalogTableType(
                meta.Identifier,
                builder.TypeName,
                builder.QueryFieldName,
                builder.FilterTypeName,
                builder.OrderByTypeName,
                meta,
                columns,
                relations));
            tablesProcessed++;
            MaybeLogBuildProgress(logger, effectiveTime, buildStartTimestamp, ref lastProgressTimestamp, tablesProcessed, phase);
        }

        if (invalidRelations.Count > 0)
        {
            logger?.LogWarning(
                "GraphQL catalog schema: {Count} relation(s) with invalid or missing join key columns are not exposed.",
                invalidRelations.Count);
            foreach (var id in invalidRelations.Take(MaxInvalidRelationDetailLines))
            {
                logger?.LogWarning("GraphQL catalog schema: relation {RelationId} not exposed (invalid join keys).", id);
            }
        }

        if (omittedRelations > 0)
        {
            logger?.LogInformation(
                "GraphQL catalog schema: {Count} relation(s) between different data sources or dialects are not exposed; cross-source GraphQL relations are not delivered yet.",
                omittedRelations);
        }

        var omittedSnapshot = omittedTables
            .OrderBy(o => o.Table.Domain, StringComparer.Ordinal)
            .ThenBy(o => o.Table.Schema, StringComparer.Ordinal)
            .ThenBy(o => o.Table.TableName, StringComparer.Ordinal)
            .ToArray();

        return (finalTables, omittedSnapshot);
    }

    /// <summary>Issue #1099: upper bound of a relation field name; longer names are cut and get a short stable hash.</summary>
    internal const int MaxRelationFieldNameLength = 128;

    private const int RelationFieldNameHashLength = 8;

    /// <summary>
    /// Issue #1099: the parent-side field of a relation that carries the default (parent) name is named after the child table;
    /// when the parent has several such relations to the same child, the child key columns are appended (<c>{child}_by_{keys}</c>).
    /// </summary>
    private static string DefaultNamedRelationFieldName(TableRelation rel, bool ambiguous) =>
        ambiguous
            ? $"{rel.ChildTableIdentifier.TableName}_by_{string.Join('_', rel.JoinKeysChild)}"
            : rel.ChildTableIdentifier.TableName;

    /// <summary>
    /// Allocates a unique relation field name. A name that collides with a column, an earlier relation field or a filter keyword
    /// gets <c>_rel</c>; further collisions get <c>_{n}</c> on that base (<c>{base}_rel_2</c>, <c>{base}_rel_3</c>, never chained,
    /// issue #1099). The next free suffix per base is remembered, so many equal names stay linear.
    /// </summary>
    private static string AllocateRelationFieldName(
        string rawName,
        HashSet<string> usedRelationFieldNames,
        Dictionary<string, int> nextSuffixByBase)
    {
        var baseName = SanitizeGraphQlName(rawName);
        var candidate = BoundRelationFieldName(baseName);
        if (usedRelationFieldNames.Contains(candidate) || ReservedFilterKeywords.Contains(candidate))
        {
            baseName += "_rel";
            candidate = BoundRelationFieldName(baseName);
        }

        if (usedRelationFieldNames.Add(candidate))
        {
            return candidate;
        }

        var suffix = nextSuffixByBase.TryGetValue(baseName, out var next) ? next : 2;
        do
        {
            candidate = BoundRelationFieldName($"{baseName}_{suffix.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            suffix++;
        }
        while (!usedRelationFieldNames.Add(candidate));

        nextSuffixByBase[baseName] = suffix;
        return candidate;
    }

    /// <summary>
    /// Issue #1099: keeps a (sanitized) field name within <see cref="MaxRelationFieldNameLength"/>: a longer name keeps its
    /// prefix and ends with <c>_</c> plus the first hex digits of its SHA-256, so the result is deterministic and distinct names stay distinct.
    /// </summary>
    internal static string BoundRelationFieldName(string name)
    {
        if (name.Length <= MaxRelationFieldNameLength)
        {
            return name;
        }

        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name), hash);
        var hex = Convert.ToHexStringLower(hash[..(RelationFieldNameHashLength / 2)]);
        return string.Concat(name.AsSpan(0, MaxRelationFieldNameLength - RelationFieldNameHashLength - 1), "_", hex);
    }

    /// <summary>
    /// Issue #1099: stable order of a parent's relations for field name allocation: child table, child key columns, parent key
    /// columns, relation name, cardinality; the (random) id only breaks ties between otherwise identical relations.
    /// </summary>
    internal sealed class RelationNamingOrder : IComparer<TableRelation>
    {
        public static readonly RelationNamingOrder Instance = new();

        public int Compare(TableRelation? x, TableRelation? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var c = CompareText(x.ChildTableIdentifier.Domain, y.ChildTableIdentifier.Domain);
            if (c == 0) c = CompareText(x.ChildTableIdentifier.Schema, y.ChildTableIdentifier.Schema);
            if (c == 0) c = CompareText(x.ChildTableIdentifier.TableName, y.ChildTableIdentifier.TableName);
            if (c == 0) c = CompareKeys(x.JoinKeysChild, y.JoinKeysChild);
            if (c == 0) c = CompareKeys(x.JoinKeysParent, y.JoinKeysParent);
            if (c == 0) c = CompareText(x.RelationName, y.RelationName);
            if (c == 0) c = x.Cardinality.CompareTo(y.Cardinality);
            return c != 0 ? c : x.Id.CompareTo(y.Id);
        }

        private static int CompareKeys(IReadOnlyList<string> x, IReadOnlyList<string> y)
        {
            var count = Math.Min(x.Count, y.Count);
            for (var i = 0; i < count; i++)
            {
                var c = CompareText(x[i], y[i]);
                if (c != 0)
                {
                    return c;
                }
            }

            return x.Count.CompareTo(y.Count);
        }

        private static int CompareText(string? x, string? y)
        {
            var c = StringComparer.OrdinalIgnoreCase.Compare(x, y);
            return c != 0 ? c : StringComparer.Ordinal.Compare(x, y);
        }
    }
}
