using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

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

public sealed class CatalogSchemaModel
{
    public IReadOnlyList<CatalogTableType> Tables { get; }
    public IReadOnlyDictionary<TableIdentifier, CatalogTableType> TablesByIdentifier { get; }
    public IReadOnlyDictionary<string, CatalogTableType> TablesByTypeName { get; }
    public IReadOnlyDictionary<string, CatalogTableType> TablesByQueryFieldName { get; }

    private CatalogSchemaModel(List<CatalogTableType> tables)
    {
        Tables = tables;
        TablesByIdentifier = tables.ToDictionary(t => t.Identifier);
        TablesByTypeName = tables.ToDictionary(t => t.TypeName, StringComparer.Ordinal);
        TablesByQueryFieldName = tables.ToDictionary(t => t.QueryFieldName, StringComparer.Ordinal);
    }

    public static async Task<CatalogSchemaModel> BuildAsync(
        ITableMetadataRepository metadataRepository,
        ITableRelationRepository relationRepository,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadataRepository);
        ArgumentNullException.ThrowIfNull(relationRepository);

        var allTables = await metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);

        // Filter: only active SQL tables with supported database dialect
        var activeSqlTables = allTables
            .Where(t => t.Table.IsActive &&
                        t.DataSourceType == DataSourceType.Sql &&
                        DatabaseDialectExtensions.TryParseDialect(t.Table.SourceType, out _))
            .OrderBy(t => t.Identifier.Domain)
            .ThenBy(t => t.Identifier.Schema)
            .ThenBy(t => t.Identifier.TableName)
            .ToList();

        var usedSchemaTypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Query", "AutherisSortDirection",
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
            var suffix = 2;

            while (usedSchemaTypeNames.Contains(typeName) ||
                   usedSchemaTypeNames.Contains(filterTypeName) ||
                   usedSchemaTypeNames.Contains(orderTypeName))
            {
                typeName = $"{baseName}_{suffix++}";
                filterTypeName = $"{typeName}_filter";
                orderTypeName = $"{typeName}_order_by";
            }

            usedSchemaTypeNames.Add(typeName);
            usedSchemaTypeNames.Add(filterTypeName);
            usedSchemaTypeNames.Add(orderTypeName);

            var queryFieldName = typeName; // Same name for root query field and object type
            var columns = new List<CatalogColumnField>();
            var usedColNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var col in meta.Columns)
            {
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

        // Map relations
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
                    if (rel.JoinKeysParent == null || rel.JoinKeysParent.Count == 0 ||
                        rel.JoinKeysChild == null || rel.JoinKeysChild.Count == 0)
                    {
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
                    if (rel.JoinKeysParent == null || rel.JoinKeysParent.Count == 0 ||
                        rel.JoinKeysChild == null || rel.JoinKeysChild.Count == 0)
                    {
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

        return new CatalogSchemaModel(finalTables);
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
