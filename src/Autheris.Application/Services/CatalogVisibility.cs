namespace Autheris.Application.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// F-CONS-04 / MCP-1: which catalog tables and columns a non-admin subject may discover. Shared by the GraphQL
/// <c>catalog</c> query and the MCP resources so both apply the same rules:
/// a table is visible with at least one active Allow consent of the subject's tenant and no unconditional Deny;
/// a column is visible when no Deny consent denies it and an Allow consent grants it (or grants the whole table).
/// </summary>
public static class CatalogVisibility
{
    public static IReadOnlyList<TableMetadata> FilterForSubject(
        IEnumerable<TableMetadata> allTables,
        IEnumerable<Consent> activeConsents,
        TenantId tenantId)
    {
        ArgumentNullException.ThrowIfNull(allTables);
        ArgumentNullException.ThrowIfNull(activeConsents);

        var tenantConsents = activeConsents.Where(c => c.TenantId == tenantId).ToList();

        var allowedTableIds = tenantConsents
            .Where(c => c.Effect == ConsentEffect.Allow)
            .Select(c => c.TableIdentifier)
            .ToHashSet();

        var unconditionallyDeniedTableIds = tenantConsents
            .Where(c => c.Effect == ConsentEffect.Deny && c.RowFilters.Count == 0 && c.ColumnRules.Count == 0)
            .Select(c => c.TableIdentifier)
            .ToHashSet();

        var consentsByTable = tenantConsents
            .GroupBy(c => c.TableIdentifier)
            .ToDictionary(g => g.Key, g => g.ToList());

        var visible = new List<TableMetadata>();
        foreach (var table in allTables)
        {
            if (!allowedTableIds.Contains(table.Identifier) || unconditionallyDeniedTableIds.Contains(table.Identifier))
            {
                continue;
            }

            var tableConsents = consentsByTable.TryGetValue(table.Identifier, out var tc) ? tc : [];
            var tableAllows = tableConsents.Where(c => c.Effect == ConsentEffect.Allow).ToList();

            var hasUnconstrainedAllow = tableAllows.Any(c => c.ColumnRules.Count == 0);
            var explicitlyGrantedColumns = tableAllows
                .SelectMany(c => c.ColumnRules)
                .Where(cr => cr.AccessLevel != ColumnAccessLevel.Deny)
                .Select(cr => cr.ColumnName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var deniedColumns = tableConsents
                .Where(c => c.Effect == ConsentEffect.Deny)
                .SelectMany(c => c.ColumnRules)
                .Where(cr => cr.AccessLevel == ColumnAccessLevel.Deny)
                .Select(cr => cr.ColumnName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var visibleColumns = table.Columns
                .Where(c => !deniedColumns.Contains(c.ColumnName) &&
                            (hasUnconstrainedAllow || explicitlyGrantedColumns.Contains(c.ColumnName)))
                .ToList();

            visible.Add(table with { Columns = visibleColumns });
        }

        return visible;
    }
}
