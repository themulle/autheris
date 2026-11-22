using System.Text;
using Autheris.Application.Analytics;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql.Tree.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

namespace Autheris.GraphQL.Catalog;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Represents GraphQL schema scalar/type names.")]
public enum CatalogFieldType
{
    String,
    Int,
    Long,
    Float,
    Decimal,
    Boolean,
    DateTime
}

public sealed record CatalogColumnField(
    string FieldName,
    string ColumnName,
    CatalogFieldType FieldType,
    string DataType,
    bool IsNullable,
    string? Description = null);

public sealed record CatalogRelationField(
    string FieldName,
    string RelationName,
    TableIdentifier TargetTableIdentifier,
    string TargetTypeName,
    bool IsList,
    IReadOnlyList<string> ParentColumns,
    IReadOnlyList<string> ChildColumns,
    RelationCardinality Cardinality = RelationCardinality.OneToMany);

public sealed record CatalogTableType(
    TableIdentifier Identifier,
    string TypeName,
    string QueryFieldName,
    string FilterTypeName,
    string OrderByTypeName,
    TableMetadata Metadata,
    IReadOnlyList<CatalogColumnField> Columns,
    IReadOnlyList<CatalogRelationField> Relations);

/// <summary>Feature attached to every catalog root field on <c>Query</c> so request middleware can inspect table and column references.</summary>
public sealed record CatalogRootFieldFeature(CatalogTableType Table, CatalogSchemaModel SchemaModel);

/// <summary>BUG #157: why an active SQL catalog table is not part of the generated GraphQL schema.</summary>
public enum CatalogTableOmissionReason
{
    NoQueryableColumns,
    NameCollision,
    UnsupportedDialect
}

/// <summary>An active SQL catalog table that was left out of the GraphQL schema, with the reason. Carries no column data.</summary>
public sealed record CatalogOmittedTable(TableIdentifier Table, CatalogTableOmissionReason Reason);

public sealed partial class CatalogSchemaModel
{
    public IReadOnlyList<CatalogTableType> Tables { get; }
    public IReadOnlyDictionary<TableIdentifier, CatalogTableType> TablesByIdentifier { get; }
    public IReadOnlyDictionary<string, CatalogTableType> TablesByTypeName { get; }
    public IReadOnlyDictionary<string, CatalogTableType> TablesByQueryFieldName { get; }

    /// <summary>BUG #157: active SQL tables left out of the schema, in ordinal table order (immutable snapshot).</summary>
    public IReadOnlyList<CatalogOmittedTable> OmittedTables { get; }

    private string? _cachedFingerprint;

    /// <summary>R-GQL-12: catalog without tables, used while the governance database is unreachable.</summary>
    public static CatalogSchemaModel Empty { get; } = new([], []);

    /// <summary>
    /// R-GQL-12: stable fingerprint of everything the generated GraphQL types depend on (type and field names, column
    /// types, masking-driven typing and relations). A different fingerprint means the schema must be rebuilt.
    /// </summary>
    public string Fingerprint()
    {
        if (_cachedFingerprint is not null)
        {
            return _cachedFingerprint;
        }

        var perTableHashes = new string[Tables.Count];
        for (int i = 0; i < Tables.Count; i++)
        {
            perTableHashes[i] = ComputeTableFingerprint(Tables[i]);
        }

        var computed = ComputeCombinedFingerprint(Tables, perTableHashes, OmittedTables);
        _cachedFingerprint = computed;
        return computed;
    }

    internal static string ComputeTableFingerprint(CatalogTableType t)
    {
        var sb = new StringBuilder();
        sb.Append(t.TypeName).Append('|').Append(t.Metadata.Table.SourceType).Append('|').Append(t.Metadata.Table.DiscoveryHidden ? '1' : '0').Append('{');
        foreach (var c in t.Columns)
        {
            sb.Append(c.FieldName).Append(':').Append(c.ColumnName).Append(':').Append(c.FieldType).Append(',');
        }

        sb.Append('}').Append('[');
        foreach (var r in t.Relations)
        {
            sb.Append(r.FieldName).Append('>').Append(r.TargetTypeName).Append(r.IsList ? "*" : "1")
              .Append('(').Append(string.Join(',', r.ParentColumns)).Append('=').Append(string.Join(',', r.ChildColumns)).Append(')');
        }

        sb.Append("];");
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    internal static string ComputeCombinedFingerprint(
        IReadOnlyList<CatalogTableType> tables,
        IReadOnlyList<string> perTableHashes,
        IReadOnlyList<CatalogOmittedTable> omittedTables)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < tables.Count; i++)
        {
            var id = tables[i].Identifier;
            sb.Append(id.Domain.Length).Append('.').Append(id.Domain)
              .Append(id.Schema.Length).Append('.').Append(id.Schema)
              .Append(id.TableName.Length).Append('.').Append(id.TableName)
              .Append('=').Append(perTableHashes[i]).Append(';');
        }

        // BUG #157: omissions are part of the fingerprint so a newly added column-less table triggers a rebuild (and the
        // warning / health list). Each name part is length-prefixed, so no name content can merge or split entries.
        foreach (var o in omittedTables)
        {
            sb.Append('!');
            sb.Append(o.Table.Domain.Length).Append('.').Append(o.Table.Domain)
              .Append(o.Table.Schema.Length).Append('.').Append(o.Table.Schema)
              .Append(o.Table.TableName.Length).Append('.').Append(o.Table.TableName);

            sb.Append(':').Append(o.Reason).Append(';');
        }

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private CatalogSchemaModel(List<CatalogTableType> tables, IReadOnlyList<CatalogOmittedTable> omittedTables, string? precomputedFingerprint = null)
    {
        Tables = tables;
        OmittedTables = omittedTables;
        _cachedFingerprint = precomputedFingerprint;
        TablesByIdentifier = tables.ToDictionary(t => t.Identifier);
        TablesByTypeName = tables.ToDictionary(t => t.TypeName, StringComparer.Ordinal);
        TablesByQueryFieldName = tables.ToDictionary(t => t.QueryFieldName, StringComparer.Ordinal);
    }

    public static Task<CatalogSchemaModel> BuildAsync(
        ITableMetadataRepository metadataRepository,
        ITableRelationRepository relationRepository,
        Microsoft.Extensions.Logging.ILogger? logger = null,
        CancellationToken ct = default)
        => BuildAsync(metadataRepository, relationRepository, logger, dialectRegistry: null, crossSourceEnabled: false, tableFilter: null, ct: ct);

    public static Task<CatalogSchemaModel> BuildFilteredAsync(
        ITableMetadataRepository metadataRepository,
        ITableRelationRepository relationRepository,
        Func<TableMetadata, bool>? tableFilter,
        Microsoft.Extensions.Logging.ILogger? logger = null,
        CancellationToken ct = default)
        => BuildAsync(metadataRepository, relationRepository, logger, dialectRegistry: null, crossSourceEnabled: false, tableFilter: tableFilter, ct: ct);

    public static Task<CatalogSchemaModel> BuildAsync(
        ITableMetadataRepository metadataRepository,
        ITableRelationRepository relationRepository,
        Microsoft.Extensions.Logging.ILogger? logger,
        Autheris.Application.Sql.Dialects.ISqlDialectRegistry? dialectRegistry,
        CancellationToken ct = default)
        => BuildAsync(metadataRepository, relationRepository, logger, dialectRegistry, crossSourceEnabled: false, ct);

    public static Task<CatalogSchemaModel> BuildAsync(
        ITableMetadataRepository metadataRepository,
        ITableRelationRepository relationRepository,
        Microsoft.Extensions.Logging.ILogger? logger,
        Autheris.Application.Sql.Dialects.ISqlDialectRegistry? dialectRegistry,
        bool crossSourceEnabled,
        CancellationToken ct = default)
        => BuildAsync(metadataRepository, relationRepository, logger, dialectRegistry, crossSourceEnabled, tableFilter: null, ct);

    public static Task<CatalogSchemaModel> BuildAsync(
        ITableMetadataRepository metadataRepository,
        ITableRelationRepository relationRepository,
        Microsoft.Extensions.Logging.ILogger? logger,
        Autheris.Application.Sql.Dialects.ISqlDialectRegistry? dialectRegistry,
        bool crossSourceEnabled,
        Func<TableMetadata, bool>? tableFilter,
        CancellationToken ct = default)
        => BuildAsync(
            metadataRepository,
            relationRepository,
            logger,
            dialectRegistry,
            crossSourceEnabled,
            tableFilter,
            fingerprintCache: null,
            epochService: null,
            timeProvider: null,
            ct: ct);

    internal static async Task<CatalogSchemaModel> BuildAsync(
        ITableMetadataRepository metadataRepository,
        ITableRelationRepository relationRepository,
        Microsoft.Extensions.Logging.ILogger? logger,
        Autheris.Application.Sql.Dialects.ISqlDialectRegistry? dialectRegistry,
        bool crossSourceEnabled,
        Func<TableMetadata, bool>? tableFilter,
        CatalogTableFingerprintCache? fingerprintCache,
        IEpochValidationService? epochService,
        TimeProvider? timeProvider,
        string phase = "check",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadataRepository);
        ArgumentNullException.ThrowIfNull(relationRepository);

        var registry = dialectRegistry ?? Autheris.Application.Sql.Dialects.ISqlDialectRegistry.Default;
        var effectiveTime = timeProvider ?? TimeProvider.System;
        long buildStartTimestamp = effectiveTime.GetTimestamp();
        long lastProgressTimestamp = buildStartTimestamp;
        int tablesProcessed = 0;

        var allTables = await Autheris.Application.Catalog.CatalogTablePageReader.ReadAllAsync(
            metadataRepository, new Autheris.Application.Catalog.CatalogTableQuery(IncludeInactive: false), ct: ct).ConfigureAwait(false);

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

        var reservedFilterKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "and", "or", "not", "where", "orderBy"
        };

        var tableBuilders = new List<(TableMetadata Metadata, string TypeName, string QueryFieldName, string FilterTypeName, string OrderByTypeName, List<CatalogColumnField> Columns)>();

        foreach (var meta in activeSqlTables)
        {
            var baseName = SanitizeGraphQlName($"{meta.Identifier.Domain}_{meta.Identifier.Schema}_{meta.Identifier.TableName}");
            var typeName = baseName;
            var filterTypeName = $"{typeName}_filter";
            var orderTypeName = $"{typeName}_order_by";
            var aggregateFieldName = $"{typeName}_aggregate";

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
                if (reservedFilterKeywords.Contains(colFieldName))
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

            var queryFieldName = typeName; // Same name for root query field and object type
            tableBuilders.Add((meta, typeName, queryFieldName, filterTypeName, orderTypeName, columns));
        }

        var tableMapById = tableBuilders.ToDictionary(
            t => t.Metadata.Identifier,
            t => t);

        // Bulk pre-fetch all relations in one query (INC-138-4) with fallback for test doubles or legacy providers
        var allRelations = await relationRepository.GetAllRelationsAsync(ct).ConfigureAwait(false);
        var relationsByTable = new Dictionary<TableIdentifier, List<TableRelation>>();
        if (allRelations != null && (allRelations.Count > 0 || relationRepository.BulkRelationsAuthoritative))
        {
            foreach (var rel in allRelations)
            {
                if (!relationsByTable.TryGetValue(rel.ParentTableIdentifier, out var list))
                {
                    list = new List<TableRelation>();
                    relationsByTable[rel.ParentTableIdentifier] = list;
                }
                list.Add(rel);
            }
        }
        else
        {
            // Fallback for repositories or test doubles that only provide GetRelationsForTableAsync
            foreach (var builder in tableBuilders)
            {
                var rels = await relationRepository.GetRelationsForTableAsync(builder.Metadata.Identifier, ct).ConfigureAwait(false);
                if (rels != null && rels.Count > 0)
                {
                    relationsByTable[builder.Metadata.Identifier] = rels.ToList();
                }

                tablesProcessed++;
                MaybeLogBuildProgress(logger, effectiveTime, buildStartTimestamp, ref lastProgressTimestamp, tablesProcessed, phase);
            }
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
        var finalTables = new List<CatalogTableType>();

        foreach (var (meta, typeName, queryFieldName, filterTypeName, orderTypeName, columns) in tableBuilders)
        {
            var relations = new List<CatalogRelationField>();
            var usedRelationFieldNames = new HashSet<string>(columns.Select(c => c.FieldName), StringComparer.Ordinal);

            if (relationsByTable.TryGetValue(meta.Identifier, out var rawRelations))
            {
                foreach (var rel in rawRelations)
                {
                    // Target table must exist in schema and have valid join keys
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
                    var relFieldName = SanitizeGraphQlName(rel.RelationName);
                    if (usedRelationFieldNames.Contains(relFieldName) || reservedFilterKeywords.Contains(relFieldName))
                    {
                        relFieldName += "_rel";
                    }
                    var rSuffix = 2;
                    while (!usedRelationFieldNames.Add(relFieldName))
                    {
                        relFieldName = $"{relFieldName}_{rSuffix++}";
                    }

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

            // Also check for reverse relations where this table is the child
            // Lookup in cached relationsByTable in memory
            reverseByChild.TryGetValue(meta.Identifier, out var reverseRelations);
            foreach (var (parentIndex, rel) in (IEnumerable<(int ParentIndex, TableRelation Relation)>?)reverseRelations ?? [])
            {
                var other = tableBuilders[parentIndex];
                {
                    if (other.Metadata.Identifier.Equals(meta.Identifier))
                    {
                        continue;
                    }
                    if (!HasValidKeys(rel, other.Metadata, meta))
                    {
                        // Already recorded by the forward loop of the parent table.
                        continue;
                    }
                    if (!RelationExecutability.IsExecutable(rel, other.Metadata, meta, crossSourceEnabled))
                    {
                        // Already counted by the forward loop of the parent table; a parent that is not in tableBuilders
                        // (inactive or not SQL) emits no relation at all, so nothing is lost by not counting here.
                        continue;
                    }

                    // Reverse: IsList is opposite (e.g. parent-to-child 1:N -> child-to-parent N:1 -> IsList = false; 1:1 stays 1:1)
                    var revCardinality = rel.Cardinality switch
                    {
                        RelationCardinality.OneToMany => RelationCardinality.ManyToOne,
                        RelationCardinality.ManyToOne => RelationCardinality.OneToMany,
                        _ => RelationCardinality.OneToOne
                    };
                    var isList = revCardinality == RelationCardinality.OneToMany;
                    var parentTableName = other.Metadata.Identifier.TableName;
                    var revFieldName = SanitizeGraphQlName($"{parentTableName}_by_{rel.RelationName}");
                    if (usedRelationFieldNames.Contains(revFieldName) || reservedFilterKeywords.Contains(revFieldName))
                    {
                        revFieldName += "_rel";
                    }
                    var revSuffix = 2;
                    while (!usedRelationFieldNames.Add(revFieldName))
                    {
                        revFieldName = $"{revFieldName}_{revSuffix++}";
                    }

                    relations.Add(new CatalogRelationField(
                        revFieldName,
                        rel.RelationName,
                        other.Metadata.Identifier,
                        other.TypeName,
                        isList,
                        rel.JoinKeysChild,  // Parent from child's perspective
                        rel.JoinKeysParent, // Child from child's perspective
                        revCardinality));
                }
            }

            finalTables.Add(new CatalogTableType(
                meta.Identifier,
                typeName,
                queryFieldName,
                filterTypeName,
                orderTypeName,
                meta,
                columns,
                relations));
            MaybeLogBuildProgress(logger, effectiveTime, buildStartTimestamp, ref lastProgressTimestamp, finalTables.Count, phase);
        }

        if (invalidRelations.Count > 0)
        {
            // one summary plus at most MaxInvalidRelationDetailLines detail lines, relation id only.
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
            // Counts only: the line is emitted on every real rebuild and must not carry catalog names.
            logger?.LogInformation(
                "GraphQL catalog schema: {Count} relation(s) between different data sources or dialects are not exposed; cross-source GraphQL relations are not delivered yet.",
                omittedRelations);
        }

        // Deterministic order independent of repository row order; immutable array so snapshots are never mutated.
        var omittedSnapshot = omittedTables
            .OrderBy(o => o.Table.Domain, StringComparer.Ordinal)
            .ThenBy(o => o.Table.Schema, StringComparer.Ordinal)
            .ThenBy(o => o.Table.TableName, StringComparer.Ordinal)
            .ToArray();

        string? precomputedFingerprint = null;
        if (fingerprintCache is not null)
        {
            IReadOnlyDictionary<TableIdentifier, long>? epochs = null;
            if (epochService is not null && finalTables.Count > 0)
            {
                try
                {
                    epochs = await epochService.GetCurrentEpochsAsync(
                        finalTables.Select(static t => t.Identifier),
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogDebug(ex, "GraphQL catalog schema build: batch epoch lookup failed; recomputing table fingerprints.");
                }
            }

            precomputedFingerprint = fingerprintCache.ComputeModelFingerprint(finalTables, omittedSnapshot, epochs);
        }

        return new CatalogSchemaModel(finalTables, omittedSnapshot, precomputedFingerprint);
    }

    private static readonly TimeSpan BuildProgressLogInterval = TimeSpan.FromSeconds(5);

    internal static void MaybeLogBuildProgress(
        Microsoft.Extensions.Logging.ILogger? logger,
        TimeProvider? timeProvider,
        long buildStartTimestamp,
        ref long lastProgressTimestamp,
        int tablesProcessed,
        string phase = "rebuild")
    {
        if (logger is null || timeProvider is null)
        {
            return;
        }

        long nowTimestamp = timeProvider.GetTimestamp();
        if (timeProvider.GetElapsedTime(lastProgressTimestamp, nowTimestamp) < BuildProgressLogInterval)
        {
            return;
        }

        lastProgressTimestamp = nowTimestamp;
        long elapsedMs = (long)timeProvider.GetElapsedTime(buildStartTimestamp, nowTimestamp).TotalMilliseconds;
        logger.LogInformation(
            "GraphQL catalog schema build progress: tablesProcessed={TablesProcessed} elapsedMs={ElapsedMs} phase={Phase}",
            tablesProcessed,
            elapsedMs,
            phase);
    }

    private const int MaxInvalidRelationDetailLines = 10;

    /// <summary>
    /// Fail closed (08): the key pairs must satisfy<see cref="RelationKeyRules"/> and every key column must
    /// still exist as a visible metadata column of its table.
    /// </summary>
    private static bool HasValidKeys(TableRelation rel, TableMetadata parent, TableMetadata child)
    {
        if (RelationKeyRules.Validate(rel.JoinKeysParent, rel.JoinKeysChild) is not null)
        {
            return false;
        }

        return AllColumnsExist(rel.JoinKeysParent, parent) && AllColumnsExist(rel.JoinKeysChild, child);
    }

    private static bool AllColumnsExist(IReadOnlyList<string> keys, TableMetadata table)
    {
        foreach (var key in keys)
        {
            var col = table.Columns?.FirstOrDefault(c => string.Equals(c.ColumnName, key, StringComparison.OrdinalIgnoreCase));
            if (col is null || col.DiscoveryHidden)
            {
                return false;
            }
        }

        return true;
    }

    public static string SanitizeGraphQlName(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "_";
        var sb = new StringBuilder();
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('_');
            }
        }
        var res = sb.ToString();
        if (res.Length == 0) return "_";
        if (char.IsDigit(res[0]))
        {
            res = "_" + res;
        }
        while (res.StartsWith("__", StringComparison.Ordinal))
        {
            res = res[1..];
        }
        return string.IsNullOrEmpty(res) ? "_" : res;
    }

    public static CatalogFieldType MapDataType(string? dataType) => ColumnValueKinds.FromDataType(dataType) switch
    {
        ColumnValueKind.Int => CatalogFieldType.Int,
        ColumnValueKind.Long => CatalogFieldType.Long,
        ColumnValueKind.Float => CatalogFieldType.Float,
        ColumnValueKind.Decimal => CatalogFieldType.Decimal,
        ColumnValueKind.Boolean => CatalogFieldType.Boolean,
        ColumnValueKind.DateTime => CatalogFieldType.DateTime,
        _ => CatalogFieldType.String
    };
}

/// <summary>
/// Per-table schema fingerprint cache keyed by <see cref="TableIdentifier"/> and validated against
/// table policy epoch and structural metadata so unchanged tables are not re-hashed on incremental updates.
/// </summary>
internal sealed class CatalogTableFingerprintCache
{
    private sealed record CachedEntry(
        long Epoch,
        bool DiscoveryHidden,
        string SourceType,
        string TypeName,
        IReadOnlyList<CatalogColumnField> Columns,
        IReadOnlyList<CatalogRelationField> Relations,
        string Hash);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<TableIdentifier, CachedEntry> _entries = new();
    private int _lastBuildRehashCount;
    private int _totalRehashCount;

    internal int LastBuildRehashCount => Volatile.Read(ref _lastBuildRehashCount);
    internal int TotalRehashCount => Volatile.Read(ref _totalRehashCount);

    internal void Invalidate(TableIdentifier id) => _entries.TryRemove(id, out _);

    internal void Clear() => _entries.Clear();

    internal bool TryGetTableHash(TableIdentifier id, out string hash)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            hash = entry.Hash;
            return true;
        }

        hash = string.Empty;
        return false;
    }

    internal string ComputeModelFingerprint(
        IReadOnlyList<CatalogTableType> tables,
        IReadOnlyList<CatalogOmittedTable> omittedTables,
        IReadOnlyDictionary<TableIdentifier, long>? epochs)
    {
        var activeIds = new HashSet<TableIdentifier>();
        var perTableHashes = new string[tables.Count];
        int rehashes = 0;

        for (int i = 0; i < tables.Count; i++)
        {
            var t = tables[i];
            activeIds.Add(t.Identifier);
            long epoch = epochs is not null && epochs.TryGetValue(t.Identifier, out var ep) ? ep : 0L;

            if (_entries.TryGetValue(t.Identifier, out var cached) && IsMatch(cached, t, epoch))
            {
                perTableHashes[i] = cached.Hash;
                continue;
            }

            string hash = CatalogSchemaModel.ComputeTableFingerprint(t);
            _entries[t.Identifier] = new CachedEntry(
                epoch,
                t.Metadata.Table.DiscoveryHidden,
                t.Metadata.Table.SourceType,
                t.TypeName,
                t.Columns,
                t.Relations,
                hash);
            perTableHashes[i] = hash;
            rehashes++;
        }

        foreach (var existingKey in _entries.Keys)
        {
            if (!activeIds.Contains(existingKey))
            {
                _entries.TryRemove(existingKey, out _);
            }
        }

        Volatile.Write(ref _lastBuildRehashCount, rehashes);
        if (rehashes > 0)
        {
            Interlocked.Add(ref _totalRehashCount, rehashes);
        }

        return CatalogSchemaModel.ComputeCombinedFingerprint(tables, perTableHashes, omittedTables);
    }

    private static bool IsMatch(CachedEntry cached, CatalogTableType current, long epoch)
    {
        if (cached.Epoch != epoch ||
            cached.DiscoveryHidden ||
            current.Metadata.Table.DiscoveryHidden ||
            !string.Equals(cached.SourceType, current.Metadata.Table.SourceType, StringComparison.Ordinal) ||
            !string.Equals(cached.TypeName, current.TypeName, StringComparison.Ordinal))
        {
            return false;
        }

        return ColumnsMatch(cached.Columns, current.Columns) &&
               RelationsMatch(cached.Relations, current.Relations);
    }

    private static bool ColumnsMatch(IReadOnlyList<CatalogColumnField> left, IReadOnlyList<CatalogColumnField> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (a.FieldType != b.FieldType ||
                !string.Equals(a.FieldName, b.FieldName, StringComparison.Ordinal) ||
                !string.Equals(a.ColumnName, b.ColumnName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RelationsMatch(IReadOnlyList<CatalogRelationField> left, IReadOnlyList<CatalogRelationField> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (a.IsList != b.IsList ||
                !string.Equals(a.FieldName, b.FieldName, StringComparison.Ordinal) ||
                !string.Equals(a.TargetTypeName, b.TargetTypeName, StringComparison.Ordinal) ||
                !StringListsMatch(a.ParentColumns, b.ParentColumns) ||
                !StringListsMatch(a.ChildColumns, b.ChildColumns))
            {
                return false;
            }
        }

        return true;
    }

    private static bool StringListsMatch(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
