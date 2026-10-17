namespace Autheris.Application.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// F-CONS-04 / MCP-1: which catalog tables and columns a non-admin subject may discover. Shared by the GraphQL
/// <c>catalog</c> query and the MCP resources so both apply the same rules:
/// a table is visible with at least one active Allow consent of the subject's tenant and no unconditional Deny;
/// a column is visible when no Deny consent denies it and an Allow consent grants it (or grants the whole table).
/// </summary>
public static class CatalogVisibility
{
    public static string ComputeAccessStatus(
        TableIdentifier table,
        IEnumerable<Consent> activeConsents,
        IEnumerable<ConsentRequest>? pendingRequests = null,
        bool isAdmin = false)
    {
        if (isAdmin)
        {
            return "GRANTED";
        }

        var hasAllow = activeConsents.Any(c =>
            c.TableIdentifier == table &&
            c.Effect == ConsentEffect.Allow);

        var hasUnconditionalDeny = activeConsents.Any(c =>
            c.TableIdentifier == table &&
            c.Effect == ConsentEffect.Deny &&
            c.RowFilters.Count == 0 &&
            c.ColumnRules.Count == 0);

        if (hasAllow && !hasUnconditionalDeny)
        {
            return "GRANTED";
        }

        if (pendingRequests != null && pendingRequests.Any(r =>
            r.TableIdentifier == table &&
            (r.Status.StartsWith("PENDING", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(r.Status, "ESCALATED", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(r.Status, "SUBMITTED", StringComparison.OrdinalIgnoreCase))))
        {
            return "PENDING";
        }

        return "NONE";
    }

    public static IReadOnlyList<TableMetadata> FilterForSubject(
        IEnumerable<TableMetadata> allTables,
        IEnumerable<Consent> activeConsents,
        TenantId tenantId,
        IReadOnlySet<string>? callerScopes = null)
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
            var isAllowed = allowedTableIds.Contains(table.Identifier) && !unconditionallyDeniedTableIds.Contains(table.Identifier);
            if (!isAllowed)
            {
                if (table.Table.Visibility == DatasetVisibility.Hidden)
                {
                    continue;
                }

                if (table.Table.Visibility == DatasetVisibility.Summary)
                {
                    visible.Add(table.ReleaseProfileToCatalogViewers
                        ? table
                        : table with { Columns = Array.Empty<TableColumn>() });
                    continue;
                }

                if (table.Table.Visibility == DatasetVisibility.Full)
                {
                    visible.Add(table);
                    continue;
                }
            }

            var tableConsents = consentsByTable.TryGetValue(table.Identifier, out var tc) ? tc : [];
            var tableAllows = tableConsents.Where(c => c.Effect == ConsentEffect.Allow).ToList();

            // Issue #127: scope-aware like the data path. A Deny column rule of any consent (SR15-52: also of an Allow consent) hides the
            // column only for callers it applies to (unscoped, or one of its scopes is held); an Allow consent shows it when its resolved level is not Deny.
            // callerScopes null or empty = base rules only (scope-only grants stay hidden, issue #116).
            var deniedColumns = tableConsents
                .SelectMany(c => c.ColumnRules)
                .Where(cr => cr.AccessLevel == ColumnAccessLevel.Deny && ConsentResolutionService.RuleAppliesToScopes(cr, callerScopes))
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
                            tableAllows.Any(a => ConsentResolutionService.ResolveConsentColumnLevel(a, c.ColumnName, callerScopes) != ColumnAccessLevel.Deny))
                .ToList();

            visible.Add(table with { Columns = visibleColumns });
        }

        return visible;
    }

    /// <summary>
    /// SR-L-21 / decision G-11: restricts <paramref name="tables"/> to those that belong to <paramref name="tenantId"/>.
    /// <c>TableMetadata</c> has no owner tenant, so ownership is derived from consents: a table is tenant-visible when the
    /// tenant holds at least one consent record on it (<paramref name="tenantConsentTables"/>, Allow or Deny) or when it is
    /// listed in <paramref name="sharedTables"/> (<c>Gateway:Catalog:SharedTables</c>, entries <c>domain.schema.table</c>).
    /// The single-tenant deployment (<see cref="TenantId.LegacySingleTenant"/>) is not restricted.
    /// </summary>
    public static IReadOnlyList<TableMetadata> FilterToTenant(
        IEnumerable<TableMetadata> tables,
        TenantId tenantId,
        IEnumerable<TableIdentifier>? tenantConsentTables,
        IEnumerable<string>? sharedTables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        if (tenantId == TenantId.LegacySingleTenant)
        {
            return tables.ToList();
        }

        var allowed = new HashSet<TableIdentifier>(tenantConsentTables ?? []);
        foreach (var shared in sharedTables ?? [])
        {
            if (TableIdentifier.TryParse(shared, out var id))
            {
                allowed.Add(id);
            }
        }

        return tables.Where(t => allowed.Contains(t.Identifier)).ToList();
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
    /// Tables <paramref name="principal"/> may discover within <paramref name="tenantId"/>. The caller class comes from
    /// <see cref="CallerScopeResolver"/>: the canonical ClusterAdmin (and, in <see cref="TenancyMode.Single"/>, a claim-less
    /// GovernanceAdmin) sees every table, a GovernanceAdmin with a tenant claim only its tenant (<see cref="FilterToTenant"/>),
    /// everybody else only tables passing <see cref="FilterForSubject"/>. Without a SID or without a consent repository nothing
    /// is visible (fail-closed). Slices results by <paramref name="contract"/> if specified. These overloads that do not pass a
    /// tenancy mode use <see cref="TenancyMode.Multi"/> (the confined view).
    /// </summary>
    public static Task<IReadOnlyList<TableMetadata>> VisibleTablesAsync(
        IReadOnlyList<TableMetadata> allTables,
        ClaimsPrincipal principal,
        TenantId tenantId,
        IConsentRepository? consentRepository,
        CancellationToken ct = default)
        => VisibleTablesAsync(allTables, principal, tenantId, consentRepository, contract: null, TenancyMode.Multi, sharedTables: null, ct);

    public static Task<IReadOnlyList<TableMetadata>> VisibleTablesAsync(
        IReadOnlyList<TableMetadata> allTables,
        ClaimsPrincipal principal,
        TenantId tenantId,
        IConsentRepository? consentRepository,
        Autheris.Application.Governance.Contracts.SchemaContractDefinition? contract,
        CancellationToken ct = default)
        => VisibleTablesAsync(allTables, principal, tenantId, consentRepository, contract, TenancyMode.Multi, sharedTables: null, ct);

    public static async Task<IReadOnlyList<TableMetadata>> VisibleTablesAsync(
        IReadOnlyList<TableMetadata> allTables,
        ClaimsPrincipal principal,
        TenantId tenantId,
        IConsentRepository? consentRepository,
        Autheris.Application.Governance.Contracts.SchemaContractDefinition? contract,
        TenancyMode tenancyMode,
        IEnumerable<string>? sharedTables,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(allTables);
        ArgumentNullException.ThrowIfNull(principal);

        var scope = CallerScopeResolver.Resolve(principal, tenancyMode);
        switch (scope)
        {
            case CallerScope.None:
                return Array.Empty<TableMetadata>();
            case CallerScope.Global:
            case CallerScope.Unrestricted:
                return FilterByContract(allTables, contract);
            case CallerScope.Tenant:
            {
                if (consentRepository == null)
                {
                    return Array.Empty<TableMetadata>();
                }

                // Same confinement rule as the GraphQL catalog (consent-derived ownership plus Gateway:Catalog:SharedTables).
                var claimTenant = principal.GetTenantId();
                var tenantConsentTables = await consentRepository.GetTablesWithConsentsForTenantAsync(claimTenant, ct).ConfigureAwait(false);
                var confined = FilterToTenant(allTables, claimTenant, tenantConsentTables, sharedTables);
                return FilterByContract(confined, contract);
            }
        }

        var roles = principal.GetUserRoles();
        var userSid = principal.GetUserSid() ?? (!string.IsNullOrWhiteSpace(principal.Identity?.Name) ? new Sid(principal.Identity.Name) : (Sid?)null);
        if (userSid == null || consentRepository == null)
        {
            return Array.Empty<TableMetadata>();
        }

        var subjects = principal.GetGroupSids().Append(userSid.Value).ToList();
        var activeConsents = await consentRepository.GetAllActiveConsentsForSubjectsAsync(
            subjects, roles, DateTimeOffset.UtcNow, tenantId, ct).ConfigureAwait(false);

        // Scopes from the claims overload, as the data path (TableAccessPolicy) reads them.
        var visible = FilterForSubject(allTables, activeConsents, tenantId, Autheris.Domain.Security.TokenAccessScope.GetScopeSet(principal.Claims));
        return FilterByContract(visible, contract);
    }
}
