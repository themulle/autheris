using System.Text;
using Autheris.Application.Interfaces;
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
    bool IsNullable);

public sealed record CatalogRelationField(
    string FieldName,
    string RelationName,
    TableIdentifier TargetTableIdentifier,
    string TargetTypeName,
    bool IsList,
    IReadOnlyList<string> ParentColumns,
    IReadOnlyList<string> ChildColumns);

public sealed record CatalogTableType(
    TableIdentifier Identifier,
    string TypeName,
    string QueryFieldName,
    string FilterTypeName,
    string OrderByTypeName,
    TableMetadata Metadata,
    IReadOnlyList<CatalogColumnField> Columns,
    IReadOnlyList<CatalogRelationField> Relations);

public sealed partial class CatalogSchemaModel
{
    public IReadOnlyList<CatalogTableType> Tables { get; }
    public IReadOnlyDictionary<TableIdentifier, CatalogTableType> TablesByIdentifier { get; }
    public IReadOnlyDictionary<string, CatalogTableType> TablesByTypeName { get; }
    public IReadOnlyDictionary<string, CatalogTableType> TablesByQueryFieldName { get; }

    /// <summary>R-GQL-12: catalog without tables, used while the governance database is unreachable.</summary>
    public static CatalogSchemaModel Empty { get; } = new([]);

    /// <summary>
    /// R-GQL-12: stable fingerprint of everything the generated GraphQL types depend on (type and field names, column
    /// types, masking-driven typing and relations). A different fingerprint means the schema must be rebuilt.
    /// </summary>
    public string Fingerprint()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var t in Tables)
        {
            sb.Append(t.TypeName).Append('|').Append(t.Metadata.Table.SourceType).Append('{');
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
        }

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private CatalogSchemaModel(List<CatalogTableType> tables)
    {
        Tables = tables;
        TablesByIdentifier = tables.ToDictionary(t => t.Identifier);
        TablesByTypeName = tables.ToDictionary(t => t.TypeName, StringComparer.Ordinal);
        TablesByQueryFieldName = tables.ToDictionary(t => t.QueryFieldName, StringComparer.Ordinal);
    }

    public static Task<CatalogSchemaModel> BuildAsync(
        ITableMetadataRepository metadataRepository,
        ITableRelationRepository relationRepository,
        Microsoft.Extensions.Logging.ILogger? logger = null,
        CancellationToken ct = default)
        => BuildAsync(metadataRepository, relationRepository, logger, dialectRegistry: null, ct);

    public static async Task<CatalogSchemaModel> BuildAsync(
        ITableMetadataRepository metadataRepository,
        ITableRelationRepository relationRepository,
        Microsoft.Extensions.Logging.ILogger? logger,
        Autheris.Application.Sql.Dialects.ISqlDialectRegistry? dialectRegistry,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadataRepository);
        ArgumentNullException.ThrowIfNull(relationRepository);

        var registry = dialectRegistry ?? Autheris.Application.Sql.Dialects.ISqlDialectRegistry.Default;
        var allTables = await metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);

        // Filter: only active SQL tables whose dialect the tree compiler supports. Every root query of this schema runs through
        // the tree compiler (Descriptor.SupportsTreeCompile, G-6), so an admitted dialect without tree support (Oracle) would
        // appear in the schema while every query failed (R-GQL-13); those tables are excluded and logged once. They stay
        // readable as flat tables through OData, REST and MCP (governed compile path), ordered deterministically
        var sqlTables = allTables
            .Where(t => t.Table.IsActive && t.DataSourceType == DataSourceType.Sql)
            .ToList();
        foreach (var excluded in sqlTables.Where(t =>
                     registry.TryGetByName(t.Table.SourceType, out var provider) &&
                     provider.Descriptor.IsExecutable &&
                     !provider.Descriptor.SupportsTreeCompile))
        {
            logger?.LogInformation(
                "GraphQL catalog schema: table '{Table}' (dialect '{Dialect}') is not exposed because the GraphQL tree compiler does not support its dialect; it stays available through OData, REST and MCP.",
                excluded.Identifier.ToQualifiedName(), excluded.Table.SourceType);
        }

        var activeSqlTables = sqlTables
            .Where(t => registry.TryGetByName(t.Table.SourceType, out var provider) &&
                        provider.Descriptor.IsExecutable &&
                        provider.Descriptor.SupportsTreeCompile)
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

            string? collidingName = null;
            if (usedSchemaTypeNames.Contains(typeName)) collidingName = typeName;
            else if (usedSchemaTypeNames.Contains(filterTypeName)) collidingName = filterTypeName;
            else if (usedSchemaTypeNames.Contains(orderTypeName)) collidingName = orderTypeName;

            if (collidingName != null)
            {
                logger?.LogError(
                    "GraphQL catalog schema: table '{Table}' conflicts with existing schema type name '{CollidingName}'. The table is omitted from GraphQL schema generation to preserve schema stability.",
                    meta.Identifier.ToQualifiedName(),
                    collidingName);
                continue;
            }

            usedSchemaTypeNames.Add(typeName);
            usedSchemaTypeNames.Add(filterTypeName);
            usedSchemaTypeNames.Add(orderTypeName);

            var queryFieldName = typeName; // Same name for root query field and object type
            var columns = new List<CatalogColumnField>();
            var usedColNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var col in meta.Columns)
            {
                // R-GQL-13: the tree compiler only accepts plain column names; others (e.g. "a-b") were offered in the
                // schema under a sanitized name but could never be queried.
                if (!QueryableColumnNameRegex().IsMatch(col.ColumnName))
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
                columns.Add(new CatalogColumnField(colFieldName, col.ColumnName, fieldType, col.DataType, true));
            }

            tableBuilders.Add((meta, typeName, queryFieldName, filterTypeName, orderTypeName, columns));
        }

        var tableMapById = tableBuilders.ToDictionary(
            t => t.Metadata.Identifier,
            t => t);

        // Pre-fetch relations for all active tables in O(N) instead of O(N^2) (R-GQL-7)
        var relationsByTable = new Dictionary<TableIdentifier, IReadOnlyList<TableRelation>>();
        foreach (var builder in tableBuilders)
        {
            var rels = await relationRepository.GetRelationsForTableAsync(builder.Metadata.Identifier, ct).ConfigureAwait(false);
            relationsByTable[builder.Metadata.Identifier] = rels ?? Array.Empty<TableRelation>();
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
                    if (!RelationExecutability.IsSameExecutionSource(meta, targetTable.Metadata))
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
                        rel.JoinKeysChild));
                }
            }

            // Also check for reverse relations where this table is the child
            // Lookup in cached relationsByTable in memory
            foreach (var other in tableBuilders)
            {
                if (other.Metadata.Identifier.Equals(meta.Identifier))
                {
                    continue;
                }

                if (!relationsByTable.TryGetValue(other.Metadata.Identifier, out var otherRelations))
                {
                    continue;
                }

                foreach (var rel in otherRelations)
                {
                    if (!rel.ChildTableIdentifier.Equals(meta.Identifier))
                    {
                        continue;
                    }
                    if (!HasValidKeys(rel, other.Metadata, meta))
                    {
                        // Already recorded by the forward loop of the parent table.
                        continue;
                    }
                    if (!RelationExecutability.IsSameExecutionSource(other.Metadata, meta))
                    {
                        // Already counted by the forward loop of the parent table; a parent that is not in tableBuilders
                        // (inactive or not SQL) emits no relation at all, so nothing is lost by not counting here.
                        continue;
                    }

                    // Reverse: IsList is opposite (e.g. parent-to-child 1:N -> child-to-parent N:1 -> IsList = false)
                    var isList = rel.Cardinality != RelationCardinality.OneToMany;
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
                        rel.JoinKeysParent)); // Child from child's perspective
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
        }

        if (invalidRelations.Count > 0)
        {
            // SEC-REL151-09: one summary plus at most MaxInvalidRelationDetailLines detail lines, relation id only.
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

        return new CatalogSchemaModel(finalTables);
    }

    private const int MaxInvalidRelationDetailLines = 10;

    /// <summary>
    /// Fail closed (SEC-REL151-03/08): the key pairs must satisfy <see cref="RelationKeyRules"/> and every key column must
    /// still exist in the metadata of its table.
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
            if (!table.HasColumn(key))
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

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial System.Text.RegularExpressions.Regex QueryableColumnNameRegex();

    public static CatalogFieldType MapDataType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return CatalogFieldType.String;
        var dt = dataType.Trim().ToLowerInvariant();

        // 1. Spatial / complex types that might contain words like 'point' or 'interval'
        if (dt.Contains("interval") || dt.Contains("point") || dt.Contains("polygon") ||
            dt.Contains("geometry") || dt.Contains("geography") || dt.Contains("line") ||
            dt.Contains("json") || dt.Contains("xml") || dt.Contains("uuid") || dt.Contains("guid"))
        {
            return CatalogFieldType.String;
        }

        var baseType = dt.Split('(', '[', ' ')[0].Trim();

        if (baseType.StartsWith("bit") || baseType.StartsWith("bool"))
        {
            return CatalogFieldType.Boolean;
        }

        if (baseType is "bigint" or "int8" or "long")
        {
            return CatalogFieldType.Long;
        }

        if (baseType is "int" or "integer" or "int4" or "smallint" or "int2" or "tinyint")
        {
            return CatalogFieldType.Int;
        }

        if (baseType is "decimal" or "numeric" or "money" or "smallmoney")
        {
            return CatalogFieldType.Decimal;
        }

        if (baseType is "float" or "double" or "real" or "float4" or "float8")
        {
            return CatalogFieldType.Float;
        }

        if (dt.Contains("date") || dt.Contains("time") || dt.Contains("timestamp"))
        {
            return CatalogFieldType.DateTime;
        }

        return CatalogFieldType.String;
    }
}
