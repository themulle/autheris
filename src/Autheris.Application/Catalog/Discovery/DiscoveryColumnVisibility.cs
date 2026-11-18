namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// The one source of column lists and column counts on the metadata discovery surfaces (MCP <c>describe_dataset</c>,
/// <c>get_table_schema</c>, <c>get_my_permissions</c>, the schema resource, GraphQL catalog discovery). A column the caller has no
/// access to is never named and never counted. The rule is fail-closed: only an explicit Clear or Masked state is visible, a missing
/// column list or an unknown state lists nothing. There is no administrator bypass: the visibility is the state computed for the
/// caller. The REST dataset detail keeps its own request-access projection and does not use this type.
/// </summary>
public static class DiscoveryColumnVisibility
{
    /// <summary>True only for a state the caller may see.</summary>
    public static bool IsVisible(DiscoveryColumnState state) => state is DiscoveryColumnState.Clear or DiscoveryColumnState.Masked;

    /// <summary>The visible columns of a discovery object; empty when the column list is not resolved.</summary>
    public static IReadOnlyList<DiscoveryColumn> VisibleColumns(DiscoveryObject? obj) =>
        obj is null ? [] : VisibleColumns(obj.Columns);

    /// <summary>The visible columns of a column list; empty when the list is null.</summary>
    public static IReadOnlyList<DiscoveryColumn> VisibleColumns(IReadOnlyList<DiscoveryColumn>? columns)
    {
        if (columns is null || columns.Count == 0)
        {
            return [];
        }

        List<DiscoveryColumn>? visible = null;
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            if (column is not null && IsVisible(column.State))
            {
                (visible ??= new List<DiscoveryColumn>(columns.Count)).Add(column);
            }
        }

        return visible ?? [];
    }

    /// <summary>The number of visible columns; 0 when every column is denied or the list is not resolved.</summary>
    public static int VisibleCount(DiscoveryObject? obj) => VisibleColumns(obj).Count;

    /// <summary>
    /// The visible columns of a REST-shaped dataset detail (any masking state except Deny). A denied column, an ungranted table (all
    /// columns listed as Deny for the request-access flow) and an empty state yield no column.
    /// </summary>
    public static IReadOnlyList<CatalogColumnDetail> VisibleColumns(CatalogDatasetDetail? detail)
    {
        if (detail?.Columns is not { Count: > 0 } columns)
        {
            return [];
        }

        return columns
            .Where(static c => c is not null && IsVisibleMaskingState(c.MaskingState))
            .ToList();
    }

    // The legacy projection reports a masking rule type as the state; any named state other than Deny is a masked view of the column.
    private static bool IsVisibleMaskingState(string? state) =>
        !string.IsNullOrWhiteSpace(state) && !string.Equals(state, "Deny", StringComparison.OrdinalIgnoreCase);
}
