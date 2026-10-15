namespace Autheris.Application.Data.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;

/// <summary>
/// Issue #94 (SD-4, SEC-94-03): a metadata copy that lists only the columns a caller may see must not carry a key or an index
/// that touches another column. Each declared key, each index and the Meta marker key survive only as a whole; a partial one is
/// dropped (never emitted as a subset), so a response cannot reveal that a hidden column is part of a key.
/// </summary>
public static class TableKeyVisibility
{
    private static readonly string[] MarkerKeys = ["primary_key_ordinal", "is_primary_key"];

    /// <summary>
    /// Returns <paramref name="metadata"/> restricted to <paramref name="visibleColumns"/>, with the definition filtered as a whole.
    /// </summary>
    public static TableMetadata Restrict(TableMetadata metadata, IReadOnlyList<TableColumn> visibleColumns)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(visibleColumns);

        var visible = new HashSet<string>(visibleColumns.Select(c => c.ColumnName), StringComparer.OrdinalIgnoreCase);
        var primaryKey = AllVisible(metadata.PrimaryKeyColumns, visible) ? metadata.PrimaryKeyColumns : Array.Empty<string>();
        var indexes = metadata.Indexes
            .Where(i => i.Columns.Count > 0 && AllVisible(i.Columns, visible))
            .ToList();

        var columns = visibleColumns;
        var markerKey = DefaultReadOrdering.ExtractMetaPrimaryKeyColumns(metadata.Columns);
        var markerKeyComplete = markerKey.Count > 0 && AllVisible(markerKey, visible);
        if (!markerKeyComplete && visibleColumns.Any(HasMarker))
        {
            // The marker key is not wholly visible (or invalid): the remaining markers would read as a partial key.
            columns = visibleColumns.Select(StripMarkers).ToList();
        }

        return metadata with { Columns = columns, PrimaryKeyColumns = primaryKey, Indexes = indexes, ReplaceKeyDefinition = false };
    }

    private static bool AllVisible(IReadOnlyList<string> names, HashSet<string> visible) =>
        names.Count > 0 && names.All(visible.Contains);

    private static bool HasMarker(TableColumn column) =>
        column.Meta.Count > 0 &&
        (MarkerKeys.Any(column.Meta.ContainsKey) ||
         (column.Meta.TryGetValue("role", out var role) && string.Equals(role, "primary_key", StringComparison.OrdinalIgnoreCase)));

    private static TableColumn StripMarkers(TableColumn column)
    {
        if (!HasMarker(column))
        {
            return column;
        }

        var meta = column.Meta
            .Where(kv => !MarkerKeys.Contains(kv.Key) &&
                         !(string.Equals(kv.Key, "role", StringComparison.Ordinal) && string.Equals(kv.Value, "primary_key", StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        return new TableColumn
        {
            Id = column.Id,
            TableId = column.TableId,
            ColumnName = column.ColumnName,
            DataType = column.DataType,
            IsSensitive = column.IsSensitive,
            Description = column.Description,
            LongDescription = column.LongDescription,
            DocumentationSource = column.DocumentationSource,
            Meta = meta
        };
    }
}
