namespace Autheris.Application.DataCatalog.Services;

using System;
using System.Collections.Generic;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Model;
using TrinoSqlEngine;

/// <summary>
/// Issue #404 D-4: turns the physical non-unique read of a table into the list the catalog stores. Pure: no I/O, no clock.
/// <list type="bullet">
/// <item>Result <c>null</c>: the read is not trustworthy (<see cref="PhysicalKeySnapshotState.Unavailable"/>), keep the stored list (I404-SC-04).</item>
/// <item>Result empty: the source is not readable for this dialect (<see cref="PhysicalKeySnapshotState.Unsupported"/>), the stored hints are cleared.</item>
/// <item>Physical column names map to catalog columns by the dialect case rule (SQL Server ignores case); an index with an unmapped column is
/// dropped whole. An index equal to the primary key, a declared index or an earlier physical index (columns and directions) is dropped;
/// prefixes are kept. The list is capped at <see cref="TableKeyDefinitionValidator.MaxPhysicalIndexesPerTable"/>.</item>
/// </list>
/// Physical indexes never feed read ordering, keysets or policy (I404-SC-09); this type only shapes metadata for exposure.
/// </summary>
public static class PhysicalNonUniqueIndexResolver
{
    /// <summary>Warning code appended (with the table id only) when the non-unique read is unavailable.</summary>
    public const string UnavailableCode = "PHYSICAL_INDEX_UNAVAILABLE";

    public static IReadOnlyList<PhysicalIndexDefinition>? Resolve(TableMetadata declared, PhysicalKeySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(declared);
        var nonUnique = snapshot.NonUnique;
        switch (nonUnique.State)
        {
            case PhysicalKeySnapshotState.Unsupported:
                return [];
            case PhysicalKeySnapshotState.Known when nonUnique.Indexes is not null:
                break;
            default:
                return null;
        }

        var comparer = snapshot.Dialect == TargetSqlDialect.SqlServer ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var catalogNames = new Dictionary<string, string>(comparer);
        foreach (var column in declared.Columns)
        {
            catalogNames.TryAdd(column.ColumnName, column.ColumnName);
        }

        var result = new List<PhysicalIndexDefinition>();
        foreach (var index in nonUnique.Indexes!)
        {
            if (!TryMap(index, catalogNames, out var mapped) || IsDeclared(declared, mapped) || Contains(result, mapped))
            {
                continue;
            }

            result.Add(mapped);
            if (result.Count == TableKeyDefinitionValidator.MaxPhysicalIndexesPerTable)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>True when both lists hold the same indexes in the same order.</summary>
    public static bool SameList(IReadOnlyList<PhysicalIndexDefinition> left, IReadOnlyList<PhysicalIndexDefinition> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].Matches(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryMap(PhysicalNonUniqueIndex index, Dictionary<string, string> catalogNames, out PhysicalIndexDefinition mapped)
    {
        mapped = null!;
        if (index.Columns.Count == 0 || index.Directions.Count != index.Columns.Count)
        {
            return false;
        }

        var columns = new List<string>(index.Columns.Count);
        foreach (var name in index.Columns)
        {
            if (!catalogNames.TryGetValue(name, out var catalogName))
            {
                return false;
            }

            columns.Add(catalogName);
        }

        mapped = new PhysicalIndexDefinition(columns, KeyDirections.Canonical(index.Directions) ?? Array.Empty<KeyColumnDirection>());
        return true;
    }

    private static bool IsDeclared(TableMetadata declared, PhysicalIndexDefinition candidate)
    {
        if (declared.PrimaryKeyColumns.Count > 0
            && candidate.Matches(new PhysicalIndexDefinition(declared.PrimaryKeyColumns, KeyDirections.Canonical(declared.PrimaryKeyDirections) ?? Array.Empty<KeyColumnDirection>())))
        {
            return true;
        }

        foreach (var index in declared.Indexes)
        {
            if (candidate.Matches(new PhysicalIndexDefinition(index.Columns, KeyDirections.Canonical(index.Directions) ?? Array.Empty<KeyColumnDirection>())))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(List<PhysicalIndexDefinition> list, PhysicalIndexDefinition candidate)
    {
        foreach (var existing in list)
        {
            if (existing.Matches(candidate))
            {
                return true;
            }
        }

        return false;
    }
}
