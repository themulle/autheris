namespace Autheris.Application.Data.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;

/// <summary>The primary key and declared indexes a caller may see (I404-SC-L1..L6).</summary>
public sealed record DatasetKeyProjection(
    IReadOnlyList<string> PrimaryKeyColumns,
    IReadOnlyList<KeyColumnDirection> PrimaryKeyDirections,
    IReadOnlyList<DatasetIndexInfo> Indexes)
{
    /// <summary>Nothing visible: the fail-closed result.</summary>
    public static DatasetKeyProjection Empty { get; } = new([], [], []);

    /// <summary>Issue #390: the key columns in key order with their direction; null when the key is not wholly readable.</summary>
    public IReadOnlyList<McpKeyColumn>? PrimaryKeyOrder =>
        PrimaryKeyColumns.Count == 0
            ? null
            : PrimaryKeyColumns.Select((c, i) => new McpKeyColumn(c, KeyDirections.IsDescending(PrimaryKeyDirections, i) ? "DESC" : "ASC")).ToList();
}

/// <summary>
/// I404-SC-L1..L6: the one projection of a declared primary key and declared indexes onto a caller. MCP <c>describe_dataset</c>
/// (and its GraphQL access block) and the REST catalog detail both use it, so no surface re-implements the filter.
/// <list type="bullet">
/// <item>A key or an index is emitted only if every one of its columns is readable in clear text for the caller; one masked, denied,
/// hidden or unresolved column omits it whole. There is no subset, no direction and no name.</item>
/// <item>The clear set must come from the caller's access levels, never from the list of listed columns. A missing set (null) or an
/// empty one omits everything (fail closed).</item>
/// <item>The cap (<see cref="MaxIndexesPerTable"/> indexes, <see cref="MaxColumnsPerKey"/> columns each) is applied after the
/// visibility filter and after de-duplication, so an omitted index can influence neither the counts nor the order.</item>
/// </list>
/// </summary>
public static class DatasetIndexProjection
{
    /// <summary>Upper bound of indexes emitted per table.</summary>
    public const int MaxIndexesPerTable = 32;

    /// <summary>Upper bound of columns emitted per key or index; a longer one is omitted whole (never truncated).</summary>
    public const int MaxColumnsPerKey = 32;

    /// <summary>
    /// Projects the declared definition of <paramref name="table"/> onto the columns in <paramref name="clearColumns"/>.
    /// </summary>
    public static DatasetKeyProjection Project(TableMetadata table, IReadOnlySet<string>? clearColumns)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (clearColumns is not { Count: > 0 })
        {
            return DatasetKeyProjection.Empty;
        }

        var pkReadable = Readable(table, table.PrimaryKeyColumns, clearColumns);
        var primaryKey = pkReadable ? table.PrimaryKeyColumns.ToArray() : [];
        IReadOnlyList<KeyColumnDirection> primaryKeyDirections = pkReadable
            ? KeyDirections.Canonical(table.PrimaryKeyDirections)?.ToArray() ?? []
            : [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indexes = new List<DatasetIndexInfo>();
        foreach (var index in table.Indexes)
        {
            if (!Readable(table, index.Columns, clearColumns))
            {
                continue;
            }

            var directions = KeyDirections.Canonical(index.Directions)?.ToArray();
            var key = (index.IsUnique ? "U:" : "N:") + string.Join('\u0001', index.Columns) + "|" + (directions == null ? "" : string.Join(',', directions));
            if (!seen.Add(key))
            {
                continue;
            }

            indexes.Add(new DatasetIndexInfo(index.Columns.ToArray(), index.IsUnique, directions));
            if (indexes.Count == MaxIndexesPerTable)
            {
                break;
            }
        }

        return new DatasetKeyProjection(primaryKey, primaryKeyDirections, indexes);
    }

    // Every name must be a real column of the table (a stale name never resolves) and Clear for the caller; no allocation.
    private static bool Readable(TableMetadata table, IReadOnlyList<string> names, IReadOnlySet<string> clear)
    {
        if (names.Count is 0 or > MaxColumnsPerKey)
        {
            return false;
        }

        for (var i = 0; i < names.Count; i++)
        {
            if (!clear.Contains(names[i]) || !IsColumnOf(table, names[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsColumnOf(TableMetadata table, string name)
    {
        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i].ColumnName, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The columns whose level is exactly <c>Clear</c>; a missing level is not clear.</summary>
    public static IReadOnlySet<string> ClearSet(IReadOnlyDictionary<string, Autheris.Domain.Interfaces.ColumnAccessLevel>? levels) =>
        levels == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                levels.Where(kv => kv.Value == Autheris.Domain.Interfaces.ColumnAccessLevel.Clear).Select(kv => kv.Key),
                StringComparer.OrdinalIgnoreCase);
}
