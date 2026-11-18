namespace Autheris.Application.DataSources.Services;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Autheris.Domain.Model;

/// <summary>
/// Issue #807: the required filter columns and column groups of a table as a catalog caller may see them. A required column
/// is named only when the caller's answer lists that column; a group only when it lists every member, so a group is never reported in
/// part and no hidden or denied column name is disclosed. Enforcement is unaffected (<see cref="RequiredFilterColumnRule"/>).
/// </summary>
public static class RequiredFilterColumnProjection
{
    /// <summary>Projects the required filters of <paramref name="table"/> onto <paramref name="listedColumns"/> (case-insensitive names).</summary>
    public static (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Groups) Project(TableMetadata table, IReadOnlySet<string> listedColumns)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(listedColumns);

        var required = table.RequiredFilterColumns;
        var groups = table.RequiredFilterColumnGroups;
        if (listedColumns.Count == 0 || (required.Length == 0 && groups.Length == 0))
        {
            return ([], []);
        }

        List<string>? columns = null;
        foreach (var column in required)
        {
            if (listedColumns.Contains(column))
            {
                (columns ??= new List<string>(required.Length)).Add(column);
            }
        }

        List<IReadOnlyList<string>>? visibleGroups = null;
        foreach (var group in groups)
        {
            if (AllListed(group, listedColumns))
            {
                (visibleGroups ??= new List<IReadOnlyList<string>>(groups.Length)).Add(group.ToArray());
            }
        }

        return (columns ?? (IReadOnlyList<string>)[], visibleGroups ?? (IReadOnlyList<IReadOnlyList<string>>)[]);
    }

    /// <summary>Issue #807: the REST catalog detail with the required filters over the columns it lists (null members when there are none).</summary>
    public static CatalogDatasetDetail WithRequiredFilters(TableMetadata table, CatalogDatasetDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in detail.Columns)
        {
            listed.Add(column.Name);
        }

        var (columns, groups) = Project(table, listed);
        return detail with
        {
            RequiredFilterColumns = columns.Count == 0 ? null : columns,
            RequiredFilterColumnGroups = groups.Count == 0 ? null : groups
        };
    }

    private static bool AllListed(ImmutableArray<string> group, IReadOnlySet<string> listedColumns)
    {
        if (group.IsDefaultOrEmpty)
        {
            return false;
        }

        foreach (var column in group)
        {
            if (!listedColumns.Contains(column))
            {
                return false;
            }
        }

        return true;
    }
}
