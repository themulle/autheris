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
        ArgumentNullException.ThrowIfNull(snapshot);

        var registry = dialectRegistry ?? Autheris.Application.Sql.Dialects.ISqlDialectRegistry.Default;
        var effectiveTime = timeProvider ?? TimeProvider.System;
        long buildStartTimestamp = effectiveTime.GetTimestamp();
        long lastProgressTimestamp = buildStartTimestamp;

        var (omittedTables, tableBuilders, tableMapById, tablesProcessed) = PrepareTableBuilders(
            snapshot.Tables,
            registry,
            tableFilter,
            fingerprintCache: null,
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

        var (finalTables, omittedSnapshot) = AssembleModelData(
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

        return new CatalogSchemaModel(finalTables, omittedSnapshot);
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

            if (!candidate.Table.IsActive || candidate.DataSourceType != DataSourceType.Sql || candidate.Table.ObjectKind == DiscoveryObjectKind.MetricView)
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

            if (relationsByTable.TryGetValue(meta.Identifier, out var rawRelations))
            {
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

                    var isList = rel.Cardinality == RelationCardinality.OneToMany;
                    var relFieldName = AllocateRelationFieldName(rel.RelationName, usedRelationFieldNames);

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
                var revFieldName = AllocateRelationFieldName($"{other.Metadata.Identifier.TableName}_by_{rel.RelationName}", usedRelationFieldNames);

                relations.Add(new CatalogRelationField(
                    revFieldName,
                    rel.RelationName,
                    other.Metadata.Identifier,
                    other.TypeName,
                    isList,
                    rel.JoinKeysChild,
                    rel.JoinKeysParent,
                    revCardinality));
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

    private static string AllocateRelationFieldName(string rawName, HashSet<string> usedRelationFieldNames)
    {
        var fieldName = SanitizeGraphQlName(rawName);
        if (usedRelationFieldNames.Contains(fieldName) || ReservedFilterKeywords.Contains(fieldName))
        {
            fieldName += "_rel";
        }

        var suffix = 2;
        while (!usedRelationFieldNames.Add(fieldName))
        {
            fieldName = $"{fieldName}_{suffix++}";
        }

        return fieldName;
    }
}
