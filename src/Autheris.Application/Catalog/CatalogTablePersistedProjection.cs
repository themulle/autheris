namespace Autheris.Application.Catalog;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Autheris.Domain.Model;

/// <summary>How a catalog write changed the persisted state of one table (the input of the version-bump decision).</summary>
public enum CatalogWriteClassification
{
    /// <summary>Nothing persisted differs: no write, no epoch, no catalog version, no event.</summary>
    Unchanged = 0,

    /// <summary>Only new tables, new columns or documentation texts changed; a deferred catalog version bump is safe.</summary>
    Additive = 1,

    /// <summary>Anything else, including every change not proven additive (fail closed): the bump must not be delayed.</summary>
    Restrictive = 2
}

/// <summary>What an upsert did to the stored table.</summary>
public enum CatalogUpsertKind
{
    Created = 0,
    Updated = 1,
    Unchanged = 2
}

/// <summary>Result of <c>UpsertTableMetadataWithResultAsync</c>: the stored metadata (freshly loaded) plus the write outcome.</summary>
public sealed record CatalogTableUpsertResult(TableMetadata Metadata, CatalogUpsertKind Kind, CatalogWriteClassification Classification);

/// <summary>The outcome of comparing two projections.</summary>
public readonly record struct CatalogProjectionDiff(CatalogWriteClassification Classification, int ChangedPathCount);

/// <summary>
/// Exactly the fields the catalog upsert persists for one table (table row, columns, masking rules, key definition),
/// flattened to ordered path/value pairs. Values are compared ordinally and case-sensitively, <c>null</c> differs from the empty string, nothing
/// is trimmed. Built from the stored state read inside the write transaction, never from a cache.
/// <see cref="Compare"/> classifies a change as additive or restrictive for the deferred version bump; every change that is not
/// proven additive is restrictive (fail closed).
/// </summary>
public sealed class CatalogTablePersistedProjection
{
    private const char ListSeparator = '\u001f';

    /// <summary>Public properties of the catalog model types that the projection covers.</summary>
    public static IReadOnlySet<string> CoveredProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Table.SourceType", "Table.SourceName", "Table.SchemaName", "Table.TableName", "Table.DisplayName", "Table.Description",
        "Table.LongDescription", "Table.Texts", "Table.DocumentationSource", "Table.Sensitivity", "Table.RequiresFourEyes", "Table.IsActive",
        "Table.DataSourceType", "Table.HttpEndpoint", "Table.PluginName", "Table.MinGroupSize", "Table.NonPersonal", "Table.RequiredFilterColumns",
        "Table.DbtUniqueId", "Table.ObjectKind", "Table.MetricViewCountMeasure",
        "TableColumn.ColumnName", "TableColumn.DisplayName", "TableColumn.DataType", "TableColumn.Collation", "TableColumn.LengthSemantics",
        "TableColumn.Role",
        "TableColumn.Description", "TableColumn.LongDescription", "TableColumn.Texts", "TableColumn.DocumentationSource",
        "TableColumn.Meta",
        "MaskingRule.RuleType", "MaskingRule.PatternOrFormat", "MaskingRule.Replacement", "MaskingRule.HmacKeyId",
        "TableMetadata.Table", "TableMetadata.Columns", "TableMetadata.ColumnMaskingRules",
        "TableMetadata.ColumnClassifications", "TableMetadata.ColumnMaskOverrides",
        "TableMetadata.PrimaryKeyColumns",
        "TableMetadata.PrimaryKeyDirections", "TableMetadata.Indexes"
    };

    /// <summary>Public properties the catalog upsert does not write (or that are derived), each with the reason it is outside the projection.</summary>
    public static IReadOnlyDictionary<string, string> NotPersistedByUpsert { get; } = BuildNotPersisted();

    /// <summary>Every column the upsert writes (<c>TABLE.column</c>) mapped to its projection path root, or to a reason it is not projected.</summary>
    public static IReadOnlyDictionary<string, string> PersistedSqlColumns { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["TABLES.id"] = "(surrogate id)",
        ["TABLES.source_type"] = "table.source_type", ["TABLES.source_name"] = "table.source_name", ["TABLES.schema_name"] = "table.schema_name",
        ["TABLES.table_name"] = "table.table_name", ["TABLES.display_name"] = "table.display_name", ["TABLES.sensitivity"] = "table.sensitivity",
        ["TABLES.requires_four_eyes"] = "table.requires_four_eyes", ["TABLES.is_active"] = "table.is_active",
        ["TABLES.data_source_type"] = "table.data_source_type", ["TABLES.http_endpoint_json"] = "table.http_endpoint",
        ["TABLES.plugin_name"] = "table.plugin_name", ["TABLES.description"] = "table.description", ["TABLES.long_description"] = "table.long_description",
        ["TABLES.doc_source"] = "table.doc_source", ["TABLES.required_filter_columns_json"] = "table.required_filter_columns",
        ["TABLES.texts_json"] = "table.texts", ["TABLES.min_group_size"] = "table.min_group_size", ["TABLES.non_personal"] = "table.non_personal",
        ["TABLES.dbt_unique_id"] = "table.dbt_unique_id", ["TABLES.object_kind"] = "table.object_kind",
        ["TABLES.metric_view_count_measure"] = "table.metric_view_count_measure",
        ["TABLE_COLUMNS.id"] = "(surrogate id)", ["TABLE_COLUMNS.table_id"] = "(foreign key)", ["TABLE_COLUMNS.column_name"] = "column[name]",
        ["TABLE_COLUMNS.data_type"] = "column.data_type",
        ["TABLE_COLUMNS.description"] = "column.description", ["TABLE_COLUMNS.long_description"] = "column.long_description",
        ["TABLE_COLUMNS.doc_source"] = "column.doc_source", ["TABLE_COLUMNS.meta_json"] = "column.meta", ["TABLE_COLUMNS.display_name"] = "column.display_name",
        ["TABLE_COLUMNS.texts_json"] = "column.texts", ["TABLE_COLUMNS.collation"] = "column.collation",
        ["TABLE_COLUMNS.length_semantics"] = "column.length_semantics", ["TABLE_COLUMNS.column_role"] = "column.role",
        ["TABLE_COLUMNS.ordinal"] = "(order of columns is not compared)"
    };

    private static readonly HashSet<string> AdditiveTableFields = new(StringComparer.Ordinal)
    {
        "table.display_name", "table.description", "table.long_description", "table.doc_source"
    };

    private static readonly HashSet<string> AdditiveColumnFields = new(StringComparer.Ordinal)
    {
        "display_name", "description", "long_description", "doc_source"
    };

    private CatalogTablePersistedProjection(IReadOnlyDictionary<string, string?> values)
    {
        Values = values;
    }

    /// <summary>The ordered path/value pairs (for diagnostics and tests; values may carry catalog text and must not be logged).</summary>
    public IReadOnlyDictionary<string, string?> Values { get; }

    /// <summary>Projects the persisted fields of <paramref name="metadata"/>. Throws on a state it cannot project exactly (callers write instead).</summary>
    public static CatalogTablePersistedProjection From(TableMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var values = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        var table = metadata.Table;

        values["table.source_type"] = table.SourceType;
        values["table.source_name"] = table.SourceName;
        values["table.schema_name"] = table.SchemaName;
        values["table.table_name"] = table.TableName;
        values["table.display_name"] = table.DisplayName;
        values["table.description"] = table.Description;
        values["table.long_description"] = table.LongDescription;
        values["table.doc_source"] = table.DocumentationSource;
        values["table.sensitivity"] = table.Sensitivity;
        values["table.requires_four_eyes"] = Flag(table.RequiresFourEyes);
        values["table.is_active"] = Flag(table.IsActive);
        values["table.data_source_type"] = ((int)table.DataSourceType).ToString(CultureInfo.InvariantCulture);
        values["table.http_endpoint"] = table.HttpEndpoint is null ? null : JsonSerializer.Serialize(table.HttpEndpoint);
        values["table.plugin_name"] = table.PluginName;
        values["table.min_group_size"] = table.MinGroupSize?.ToString(CultureInfo.InvariantCulture);
        values["table.non_personal"] = Flag(table.NonPersonal);
        values["table.dbt_unique_id"] = table.DbtUniqueId;
        values["table.object_kind"] = table.ObjectKind switch
        {
            DiscoveryObjectKind.Table => "table",
            DiscoveryObjectKind.View => "view",
            DiscoveryObjectKind.MetricView => "metric_view",
            DiscoveryObjectKind.Procedure => "procedure",
            _ => throw new ArgumentOutOfRangeException(nameof(metadata), table.ObjectKind, "Unknown DiscoveryObjectKind.")
        };
        values["table.metric_view_count_measure"] = table.MetricViewCountMeasure;
        values["table.required_filter_columns"] = table.RequiredFilterColumns.IsDefault
            ? string.Empty
            : string.Join(ListSeparator, table.RequiredFilterColumns);
        AddTexts(values, "table.texts", table.Texts);

        var primaryKey = metadata.PrimaryKeyColumns;
        values["key.primary.count"] = primaryKey.Count.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < primaryKey.Count; i++)
        {
            values[$"key.primary[{i}].column"] = primaryKey[i];
            values[$"key.primary[{i}].descending"] = Flag(KeyDirections.IsDescending(metadata.PrimaryKeyDirections, i));
        }

        values["key.index.count"] = metadata.Indexes.Count.ToString(CultureInfo.InvariantCulture);
        foreach (var index in metadata.Indexes)
        {
            var prefix = $"key.index[{Escape(index.Name)}]";
            if (values.ContainsKey(prefix + ".unique"))
            {
                throw new InvalidOperationException("The key definition contains duplicate index names.");
            }

            values[prefix + ".unique"] = Flag(index.IsUnique);
            values[prefix + ".columns"] = string.Join(ListSeparator, index.Columns);
            values[prefix + ".descending"] = string.Join(ListSeparator, Enumerable.Range(0, index.Columns.Count)
                .Select(i => Flag(KeyDirections.IsDescending(index.Directions, i))));
        }

        foreach (var column in metadata.Columns)
        {
            var prefix = $"column[{Escape(column.ColumnName)}]";
            if (values.ContainsKey(prefix + ".present"))
            {
                throw new InvalidOperationException("The table contains duplicate column names.");
            }

            values[prefix + ".present"] = "1";
            values[prefix + ".data_type"] = column.DataType;
            values[prefix + ".collation"] = column.Collation;
            values[prefix + ".length_semantics"] = column.LengthSemantics;
            values[prefix + ".role"] = column.Role?.ToWireName();
            values[prefix + ".display_name"] = column.DisplayName;
            values[prefix + ".description"] = column.Description;
            values[prefix + ".long_description"] = column.LongDescription;
            values[prefix + ".doc_source"] = column.DocumentationSource;
            AddTexts(values, prefix + ".texts", column.Texts);
            foreach (var (key, value) in column.Meta)
            {
                if (!values.TryAdd($"{prefix}.meta[{Escape(key)}]", value))
                {
                    throw new InvalidOperationException("The column metadata contains duplicate keys.");
                }
            }

            if (metadata.ColumnMaskingRules.TryGetValue(column.ColumnName, out var rule))
            {
                values[prefix + ".mask.present"] = "1";
                values[prefix + ".mask.rule_type"] = rule.RuleType;
                values[prefix + ".mask.pattern_or_format"] = rule.PatternOrFormat;
                values[prefix + ".mask.replacement"] = rule.Replacement;
                values[prefix + ".mask.hmac_key_id"] = rule.HmacKeyId;
            }

            ProjectColumnClassifications(values, prefix, column.ColumnName, metadata.ColumnClassifications);
            ProjectColumnMaskOverrides(values, prefix, column.ColumnName, metadata.ColumnMaskOverrides);
        }

        foreach (var ruleColumn in metadata.ColumnMaskingRules.Keys)
        {
            if (!metadata.Columns.Any(c => string.Equals(c.ColumnName, ruleColumn, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("A masking rule refers to a column that is not part of the table.");
            }
        }

        ValidateGlobalColumnKeysExist(metadata.Columns, metadata.ColumnClassifications, c => ClassificationScopes.IsGlobal(c.Scope), "classification");
        ValidateGlobalColumnKeysExist(metadata.Columns, metadata.ColumnMaskOverrides, o => ClassificationScopes.IsGlobal(o.Scope), "mask override");

        return new CatalogTablePersistedProjection(values);
    }

    /// <summary>
    /// Compares the stored state before a write with the state after it. <paramref name="before"/> null means the table did not exist.
    /// Any difference that is not a new table, a new column or a documentation text is <see cref="CatalogWriteClassification.Restrictive"/>.
    /// </summary>
    public static CatalogProjectionDiff Compare(CatalogTablePersistedProjection? before, CatalogTablePersistedProjection after)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (before is null)
        {
            return new CatalogProjectionDiff(CatalogWriteClassification.Additive, after.Values.Count);
        }

        var changed = new List<string>();
        foreach (var (path, value) in after.Values)
        {
            if (!before.Values.TryGetValue(path, out var previous) || !string.Equals(previous, value, StringComparison.Ordinal))
            {
                changed.Add(path);
            }
        }

        foreach (var path in before.Values.Keys)
        {
            if (!after.Values.ContainsKey(path))
            {
                changed.Add(path);
            }
        }

        if (changed.Count == 0)
        {
            return new CatalogProjectionDiff(CatalogWriteClassification.Unchanged, 0);
        }

        var result = CatalogWriteClassification.Additive;
        foreach (var path in changed)
        {
            if (!IsAdditivePath(path, before, after))
            {
                result = CatalogWriteClassification.Restrictive;
                break;
            }
        }

        return new CatalogProjectionDiff(result, changed.Count);
    }

    private static bool IsAdditivePath(string path, CatalogTablePersistedProjection before, CatalogTablePersistedProjection after)
    {
        if (path.StartsWith("table.", StringComparison.Ordinal))
        {
            return AdditiveTableFields.Contains(path) || path.StartsWith("table.texts[", StringComparison.Ordinal);
        }

        if (!TrySplitColumnPath(path, out var columnPrefix, out var field))
        {
            return false;
        }

        // A column that exists only after the write is new: everything about it is additive.
        if (!before.Values.ContainsKey(columnPrefix + ".present") && after.Values.ContainsKey(columnPrefix + ".present"))
        {
            return true;
        }

        return AdditiveColumnFields.Contains(field) || field.StartsWith("texts[", StringComparison.Ordinal);
    }

    private static bool TrySplitColumnPath(string path, out string columnPrefix, out string field)
    {
        columnPrefix = string.Empty;
        field = string.Empty;
        const string start = "column[";
        if (!path.StartsWith(start, StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = start.Length; i < path.Length; i++)
        {
            if (path[i] == '\\')
            {
                i++;
                continue;
            }

            if (path[i] == ']' && i + 1 < path.Length && path[i + 1] == '.')
            {
                columnPrefix = path[..(i + 1)];
                field = path[(i + 2)..];
                return true;
            }
        }

        return false;
    }

    private static void AddTexts(SortedDictionary<string, string?> values, string prefix, LocalizedTexts? texts)
    {
        if (texts is null)
        {
            return;
        }

        foreach (var (language, entry) in texts.Entries)
        {
            values[$"{prefix}[{Escape(language)}].display_name"] = entry?.DisplayName;
            values[$"{prefix}[{Escape(language)}].description"] = entry?.Description;
            values[$"{prefix}[{Escape(language)}].long_description"] = entry?.LongDescription;
        }
    }

    private static void ProjectColumnClassifications(
        SortedDictionary<string, string?> values,
        string prefix,
        string columnName,
        IReadOnlyDictionary<string, IReadOnlyList<ColumnClassification>> allClassifications)
    {
        if (!allClassifications.TryGetValue(columnName, out var list) || list.Count == 0)
        {
            return;
        }

        foreach (var item in list.Where(c => ClassificationScopes.IsGlobal(c.Scope)).OrderBy(c => c.Code, StringComparer.Ordinal))
        {
            var key = $"{prefix}.classification[{Escape(item.Code)}]";
            if (!values.TryAdd(key, item.Source))
            {
                throw new InvalidOperationException("A column contains duplicate global classification codes.");
            }
        }
    }

    private static void ProjectColumnMaskOverrides(
        SortedDictionary<string, string?> values,
        string prefix,
        string columnName,
        IReadOnlyDictionary<string, IReadOnlyList<ColumnMaskOverride>> allOverrides)
    {
        if (!allOverrides.TryGetValue(columnName, out var list) || list.Count == 0)
        {
            return;
        }

        foreach (var item in list.Where(o => ClassificationScopes.IsGlobal(o.Scope)).OrderBy(o => o.Code ?? string.Empty, StringComparer.Ordinal))
        {
            var slot = item.Code is null ? "_all" : Escape(item.Code);
            var overridePrefix = $"{prefix}.override[{slot}]";
            if (!values.TryAdd(overridePrefix + ".present", "1"))
            {
                throw new InvalidOperationException("A column contains duplicate global mask override targets.");
            }

            values[overridePrefix + ".mask_function"] = ClassificationMaskFunctions.ToCode(item.MaskFunction);
            values[overridePrefix + ".params_json"] = item.ParamsJson;
            values[overridePrefix + ".key_ref"] = item.KeyRef;
            values[overridePrefix + ".catalog_text"] = ClassificationCatalogTextVisibilities.ToCode(item.CatalogText);
        }
    }

    private static void ValidateGlobalColumnKeysExist<T>(
        IReadOnlyList<TableColumn> columns,
        IReadOnlyDictionary<string, IReadOnlyList<T>> map,
        Func<T, bool> isGlobal,
        string kind)
    {
        foreach (var (colName, items) in map)
        {
            if (items.Any(isGlobal) && !columns.Any(c => string.Equals(c.ColumnName, colName, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"A column {kind} refers to a column that is not part of the table.");
            }
        }
    }

    private static string Flag(bool value) => value ? "1" : "0";

    private static string Escape(string? segment)
    {
        if (segment is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(segment.Length);
        foreach (var c in segment)
        {
            if (c is '\\' or ']')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static IReadOnlyDictionary<string, string> BuildNotPersisted()
    {
        const string surrogate = "surrogate id or foreign key; not catalog content";
        const string overlay = "stored in the dataset catalog settings (tags and discovery flags), not by the metadata upsert";
        const string notWritten = "not written by the metadata upsert";
        const string derived = "derived from projected properties";
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Table.Id"] = surrogate, ["TableColumn.Id"] = surrogate, ["TableColumn.TableId"] = surrogate,
            ["MaskingRule.Id"] = surrogate, ["MaskingRule.TableColumnId"] = surrogate,
            ["Table.EnforcementMode"] = notWritten, ["Table.PhysicalDatabaseName"] = notWritten, ["Table.Location"] = notWritten,
            ["Table.RlsExpression"] = notWritten, ["Table.SmallGroupStrategy"] = notWritten, ["Table.RequiredFilterColumnGroups"] = notWritten,
            ["Table.ExpectedRows"] = notWritten, ["Table.Tags"] = overlay,
            ["Table.DiscoveryHidden"] = overlay, ["Table.SamplesDisabled"] = overlay,
            ["Table.IsDelegatedSource"] = derived, ["Table.IsHighlySensitive"] = derived, ["Table.Dialect"] = derived,
            ["TableColumn.Name"] = derived, ["TableColumn.Semantics"] = derived, ["TableColumn.WriteProtected"] = notWritten,
            ["TableColumn.DiscoveryHidden"] = overlay,
            ["MaskingRule.IsHmac"] = derived, ["MaskingRule.Mode"] = notWritten, ["MaskingRule.Decimals"] = notWritten,
            ["MaskingRule.RadiusMeters"] = notWritten, ["MaskingRule.KeepPrefix"] = notWritten, ["MaskingRule.KeepSuffix"] = notWritten,
            ["MaskingRule.MaskChar"] = notWritten, ["MaskingRule.FixedLength"] = notWritten, ["MaskingRule.TokenDomain"] = notWritten,
            ["TableMetadata.Identifier"] = "persisted as Table.SourceName, SchemaName and TableName, which are projected",
            ["TableMetadata.EnforcementMode"] = derived, ["TableMetadata.IsDelegatedSource"] = derived, ["TableMetadata.OwnerTenantId"] = notWritten,
            ["TableMetadata.CatalogView"] = "set by the consent filter at read time", ["TableMetadata.CatalogTags"] = overlay,
            ["TableMetadata.ReplaceKeyDefinition"] = "write intent, not persisted", ["TableMetadata.ReplaceColumns"] = "write intent, not persisted",
            ["TableMetadata.EstimatedRowCount"] = notWritten,
            ["TableMetadata.ExpectedRows"] = notWritten, ["TableMetadata.IngestedAt"] = notWritten, ["TableMetadata.AsOf"] = notWritten,
            ["TableMetadata.EffectiveExpectedRows"] = derived, ["TableMetadata.MinGroupSize"] = notWritten, ["TableMetadata.NonPersonal"] = notWritten,
            ["TableMetadata.SmallGroupStrategy"] = notWritten, ["TableMetadata.EffectiveMinGroupSize"] = derived,
            ["TableMetadata.EffectiveNonPersonal"] = derived, ["TableMetadata.EffectiveSmallGroupStrategy"] = derived,
            ["TableMetadata.IsCompositePrimaryKey"] = derived, ["TableMetadata.DataSourceType"] = derived,
            ["TableMetadata.PhysicalDatabaseName"] = derived, ["TableMetadata.HttpEndpoint"] = derived, ["TableMetadata.PluginName"] = derived,
            ["TableMetadata.RequiredFilterColumns"] = derived, ["TableMetadata.RequiredFilterColumnGroups"] = derived,
            ["TableMetadata.Dialect"] = derived, ["TableMetadata.TenantColumnName"] = derived, ["TableMetadata.DeletedKeyPolicy"] = notWritten
        };
    }
}
