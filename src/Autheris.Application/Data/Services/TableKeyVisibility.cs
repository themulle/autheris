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
    /// <summary>
    /// Returns <paramref name="metadata"/> restricted to <paramref name="visibleColumns"/>, with the definition filtered as a whole.
    /// </summary>
    public static TableMetadata Restrict(TableMetadata metadata, IReadOnlyList<TableColumn> visibleColumns)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(visibleColumns);

        var visible = new HashSet<string>(visibleColumns.Select(c => c.ColumnName), StringComparer.OrdinalIgnoreCase);
        var primaryKeyVisible = AllVisible(metadata.PrimaryKeyColumns, visible);
        var primaryKey = primaryKeyVisible ? metadata.PrimaryKeyColumns : Array.Empty<string>();
        // Issue #390 (I390-SC-11): the directions leave with the key; a partial key never reveals a direction.
        var primaryKeyDirections = primaryKeyVisible ? metadata.PrimaryKeyDirections : Array.Empty<KeyColumnDirection>();
        var indexes = metadata.Indexes
            .Where(i => i.Columns.Count > 0 && AllVisible(i.Columns, visible))
            .ToList();

        var columns = visibleColumns;
        // Markers are a declaration channel only while no declared primary key rows exist (declared rows win). They stay on the
        // visible columns only when they still form a complete, valid key there; otherwise the remaining markers would read as
        // a partial key once the (filtered) declared key is empty.
        var markersUsable = primaryKey.Count > 0;
        if (!markersUsable && metadata.PrimaryKeyColumns.Count == 0)
        {
            var markers = TableKeyMarkers.Parse(metadata.Columns);
            markersUsable = markers.IsValid && AllVisible(markers.Columns, visible);
        }

        if (!markersUsable && visibleColumns.Any(TableKeyMarkers.HasMarker))
        {
            columns = visibleColumns.Select(StripMarkers).ToList();
        }

        return metadata with { Columns = columns, PrimaryKeyColumns = primaryKey, PrimaryKeyDirections = primaryKeyDirections, Indexes = indexes, ReplaceKeyDefinition = false };
    }

    private static bool AllVisible(IReadOnlyList<string> names, HashSet<string> visible) =>
        names.Count > 0 && names.All(visible.Contains);

    private static TableColumn StripMarkers(TableColumn column)
    {
        if (!TableKeyMarkers.HasMarker(column))
        {
            return column;
        }

        var meta = column.Meta
            .Where(kv => !TableKeyMarkers.IsMarkerEntry(kv.Key, kv.Value))
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
