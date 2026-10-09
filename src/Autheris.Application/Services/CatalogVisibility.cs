namespace Autheris.Application.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
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
                .SelectMany(c => c.ColumnRules)
                .Where(cr => cr.AccessLevel == ColumnAccessLevel.Deny)
                .Select(cr => cr.ColumnName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (table.ColumnMaskingRules != null)
            {
                foreach (var (colName, rule) in table.ColumnMaskingRules)
                {
                    if (string.Equals(rule.RuleType, "DENY", StringComparison.OrdinalIgnoreCase))
                    {
                        deniedColumns.Add(colName);
                    }
                }
            }

            var visibleColumns = table.Columns
                .Where(c => !deniedColumns.Contains(c.ColumnName) &&
                            (hasUnconstrainedAllow || explicitlyGrantedColumns.Contains(c.ColumnName)))
                .ToList();

            visible.Add(table with { Columns = visibleColumns });
        }

        return visible;
    }

    public static IReadOnlyList<TableMetadata> FilterByContract(
        IEnumerable<TableMetadata> tables,
        Autheris.Application.Governance.Contracts.SchemaContractDefinition? contract)
    {
        if (contract == null)
        {
            return tables.ToList();
        }

        return tables.Where(t =>
        {
            if (contract.AllowedTables.Count > 0 &&
                !contract.AllowedTables.Contains(t.Identifier.ToString()) &&
                !contract.AllowedTables.Contains(t.Identifier.TableName))
            {
                return false;
            }

            var allTableTags = (t.Table.Tags ?? Array.Empty<string>())
                .Concat(string.IsNullOrWhiteSpace(t.Table.Sensitivity) ? Array.Empty<string>() : [t.Table.Sensitivity])
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            if (contract.ExcludedTags.Count > 0 &&
                allTableTags.Any(tag => contract.ExcludedTags.Contains(tag)))
            {
                return false;
            }

            if (contract.IncludedTags.Count > 0 &&
                (allTableTags.Count == 0 || !allTableTags.Any(tag => contract.IncludedTags.Contains(tag))))
            {
                return false;
            }

            return true;
        }).ToList();
    }

    /// <summary>
    /// Tables <paramref name="principal"/> may discover within <paramref name="tenantId"/>: GovernanceAdmin and ClusterAdmin
    /// see every table, everybody else only tables passing <see cref="FilterForSubject"/>. Without a SID or without a consent
    /// repository nothing is visible (fail-closed). Slices results by <paramref name="contract"/> if specified.
    /// </summary>
    public static Task<IReadOnlyList<TableMetadata>> VisibleTablesAsync(
        IReadOnlyList<TableMetadata> allTables,
        ClaimsPrincipal principal,
        TenantId tenantId,
        IConsentRepository? consentRepository,
        CancellationToken ct = default)
        => VisibleTablesAsync(allTables, principal, tenantId, consentRepository, contract: null, ct);

    public static async Task<IReadOnlyList<TableMetadata>> VisibleTablesAsync(
        IReadOnlyList<TableMetadata> allTables,
        ClaimsPrincipal principal,
        TenantId tenantId,
        IConsentRepository? consentRepository,
        Autheris.Application.Governance.Contracts.SchemaContractDefinition? contract,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(allTables);
        ArgumentNullException.ThrowIfNull(principal);

        var roles = principal.GetUserRoles();
        if (roles.Contains("GovernanceAdmin") || roles.Contains("ClusterAdmin"))
        {
            return FilterByContract(allTables, contract);
        }

        var userSid = principal.GetUserSid();
        if (userSid == null || consentRepository == null)
        {
            return Array.Empty<TableMetadata>();
        }

        var subjects = principal.GetGroupSids().Append(userSid.Value).ToList();
        var activeConsents = await consentRepository.GetAllActiveConsentsForSubjectsAsync(
            subjects, roles, DateTimeOffset.UtcNow, tenantId, ct).ConfigureAwait(false);

        var visible = FilterForSubject(allTables, activeConsents, tenantId);
        return FilterByContract(visible, contract);
    }
}
