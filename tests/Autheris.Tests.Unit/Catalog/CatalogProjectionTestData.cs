namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Autheris.Domain.Model;

/// <summary>Builders for the catalog projection tests (#674).</summary>
internal static class CatalogProjectionTestData
{
    public static TableColumn Column(
        string name,
        string dataType = "varchar",
        bool isSensitive = false,
        string? description = null,
        string? collation = null,
        IReadOnlyDictionary<string, string>? meta = null) => new()
    {
        ColumnName = name,
        DataType = dataType,
        IsSensitive = isSensitive,
        Description = description,
        Collation = collation,
        Meta = meta ?? new Dictionary<string, string>()
    };

    public static TableMetadata Meta(
        IReadOnlyList<TableColumn>? columns = null,
        IReadOnlyDictionary<string, MaskingRule>? masks = null,
        Table? table = null,
        IReadOnlyList<string>? primaryKey = null,
        IReadOnlyList<TableIndexDefinition>? indexes = null)
    {
        var baseTable = table ?? new Table
        {
            SourceType = "PostgreSQL",
            SourceName = "sales",
            SchemaName = "crm",
            TableName = "customers",
            DisplayName = "Customers",
            Description = "Customer master data",
            Sensitivity = "NORMAL",
            IsActive = true
        };

        return new TableMetadata
        {
            Table = baseTable,
            Identifier = new TableIdentifier(baseTable.SourceName, baseTable.SchemaName, baseTable.TableName),
            Columns = columns ?? [Column("id", "int"), Column("name")],
            ColumnMaskingRules = masks ?? new Dictionary<string, MaskingRule>(StringComparer.Ordinal),
            PrimaryKeyColumns = primaryKey ?? ["id"],
            Indexes = indexes ?? []
        };
    }

    public static IReadOnlyDictionary<string, MaskingRule> Mask(string column, string ruleType = "REDACT", string? replacement = null) =>
        new Dictionary<string, MaskingRule>(StringComparer.Ordinal)
        {
            [column] = new MaskingRule { RuleType = ruleType, Replacement = replacement }
        };

    public static ImmutableArray<string> Filters(params string[] columns) => [.. columns];
}
