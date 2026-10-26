namespace Autheris.Application.Services;

using Autheris.Application.Dbt.Services;
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
using Autheris.Domain.Tenancy;

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

    /// <summary>
    /// Tables a non-admin subject may discover. A table with an active Allow consent (and no unconditional Deny) takes the consent path
    /// (<see cref="CatalogView.Granted"/>, columns per consent). Issue #111: persisted tiers apply only on <see cref="CatalogSurface.Discovery"/>
    /// (the default <see cref="CatalogSurface.DataAccess"/> never executes the tier switch: a table without Allow consent is dropped), and only
    /// for the tables <paramref name="sharedTables"/> names (null or empty: tiers off; consents never extend the set) or, for the catalog owner
    /// (<paramref name="catalogOwner"/>, the default tenant), for every table. Every returned row carries an explicit
    /// <see cref="CatalogView"/>; <c>Summary</c> never carries columns, <c>Full</c> carries the columns minus every DENY-masked column.
    /// </summary>
    public static IReadOnlyList<TableMetadata> FilterForSubject(
        IEnumerable<TableMetadata> allTables,
        IEnumerable<Consent> activeConsents,
        TenantId tenantId,
        IReadOnlySet<string>? callerScopes = null,
        CatalogSurface surface = CatalogSurface.DataAccess,
        IEnumerable<string>? sharedTables = null,
        TenantId? catalogOwner = null)
    {
        ArgumentNullException.ThrowIfNull(allTables);
        ArgumentNullException.ThrowIfNull(activeConsents);

        // #112 A1-13 (defence in depth): a foreign tenant's virtual table is dropped before any consent or tier is looked at.
        allTables = DropForeignVirtualTables(allTables, tenantId);

        var tiersApply = surface == CatalogSurface.Discovery;
        HashSet<TableIdentifier>? tierTables = null;
        var allTablesTiered = false;
        if (tiersApply)
        {
            if (CallerScopeResolver.IsOwner(tenantId, catalogOwner))
            {
                allTablesTiered = true;
            }
            else
            {
                tierTables = ParseSharedTables(sharedTables);
            }
        }

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
                // DataAccess: no tier switch at all. Discovery: only Summary and Full on tier-eligible tables; Hidden and every unknown value drops.
                if (tiersApply && (allTablesTiered || (tierTables?.Contains(table.Identifier) ?? false)))
                {
                    switch (table.Table.Visibility)
                    {
                        case DatasetVisibility.Summary:
                            visible.Add(table with { Columns = Array.Empty<TableColumn>(), CatalogView = CatalogView.Summary });
                            break;
                        case DatasetVisibility.Full:
                            visible.Add(table with { Columns = ColumnsWithoutDeny(table), CatalogView = CatalogView.Full });
                            break;
                    }
                }

                continue;
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

            visible.Add(table with { Columns = visibleColumns, CatalogView = CatalogView.Granted });
        }

        return visible;
    }

    /// <summary>Columns of <paramref name="table"/> minus every column with a table-level DENY masking rule (issue #111 G-2).</summary>
    private static List<TableColumn> ColumnsWithoutDeny(TableMetadata table)
    {
        if (table.ColumnMaskingRules is not { Count: > 0 } rules)
        {
            return table.Columns.ToList();
        }

        var denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (colName, rule) in rules)
        {
            if (string.Equals(rule.RuleType, "DENY", StringComparison.OrdinalIgnoreCase))
            {
                denied.Add(colName);
            }
        }

        return table.Columns.Where(c => !denied.Contains(c.ColumnName)).ToList();
    }

    /// <summary>Parses <c>domain.schema.table</c> entries like <see cref="FilterToTenant"/>; malformed entries are skipped (never widen).</summary>
    private static HashSet<TableIdentifier>? ParseSharedTables(IEnumerable<string>? sharedTables)
    {
        if (sharedTables is null)
        {
            return null;
        }

        HashSet<TableIdentifier>? parsed = null;
        foreach (var shared in sharedTables)
        {
            if (!string.IsNullOrWhiteSpace(shared) && TableIdentifier.TryParse(shared, out var id))
            {
                (parsed ??= []).Add(id);
            }
        }

        return parsed;
    }

    /// <summary>
    /// #112 A1-13 (SEC-112-15, -22, -23): a table of the semantic catalog domain (the virtual tables a manifest import creates) stays only
    /// when it lives in the <c>t_&lt;hash&gt;</c> schema of <paramref name="tenantId"/>; every other one, including a malformed or empty
    /// schema, is dropped. The rule applies to every tenant, the default tenant (catalog owner) included (DT-SC-08: virtual tables are
    /// tenant-scoped objects, only physical metadata is widened). The one place that recognises a virtual table is <see cref="SemanticVirtualTables"/>.
    /// </summary>
    internal static IReadOnlyList<TableMetadata> DropForeignVirtualTables(IEnumerable<TableMetadata> tables, TenantId tenantId)
    {
        ArgumentNullException.ThrowIfNull(tables);
        return tables
            .Where(t => !SemanticVirtualTables.IsInSemanticDomain(t.Identifier) || SemanticVirtualTables.BelongsToTenant(t, tenantId))
            .ToList();
    }

    /// <summary>
    /// SR-L-21 / decision G-11: restricts <paramref name="tables"/> to those that belong to <paramref name="tenantId"/>.
    /// <c>TableMetadata</c> has no owner tenant, so ownership is derived from consents: a table is tenant-visible when the
    /// tenant holds at least one consent record on it (<paramref name="tenantConsentTables"/>, Allow or Deny) or when it is
    /// listed in <paramref name="sharedTables"/> (<c>Gateway:Catalog:SharedTables</c>, entries <c>domain.schema.table</c>).
    /// The catalog owner (<paramref name="catalogOwner"/>, the default tenant) is not restricted (physical metadata only).
    /// </summary>
    public static IReadOnlyList<TableMetadata> FilterToTenant(
        IEnumerable<TableMetadata> tables,
        TenantId tenantId,
        IEnumerable<TableIdentifier>? tenantConsentTables,
        IEnumerable<string>? sharedTables,
        TenantId? catalogOwner = null)
    {
        ArgumentNullException.ThrowIfNull(tables);

        // #112 A1-13 (defence in depth): consents and SharedTables never re-admit a foreign tenant's virtual table.
        tables = DropForeignVirtualTables(tables, tenantId);

        if (CallerScopeResolver.IsOwner(tenantId, catalogOwner))
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
    /// <see cref="CallerScopeResolver"/>: the canonical ClusterAdmin sees every table, the GovernanceAdmin of the catalog owner
    /// (<c>catalogOwner</c>, the default tenant) every physical table of its own tenant view (foreign virtual tables dropped), a
    /// GovernanceAdmin of another tenant only its tenant (<see cref="FilterToTenant"/>), everybody else only tables passing
    /// <see cref="FilterForSubject"/>. A principal whose tenant is unresolved sees nothing. Without a SID or without a consent repository
    /// nothing is visible (fail-closed). Slices results by <paramref name="contract"/> if specified. The overloads that do not pass a
    /// catalog owner have none (the confined view).
    /// </summary>
    public static Task<IReadOnlyList<TableMetadata>> VisibleTablesAsync(
        IReadOnlyList<TableMetadata> allTables,
        ClaimsPrincipal principal,
        TenantId tenantId,
        IConsentRepository? consentRepository,
        CancellationToken ct = default)
        => VisibleTablesAsync(allTables, principal, tenantId, consentRepository, contract: null, catalogOwner: null, sharedTables: null, ct);

    public static Task<IReadOnlyList<TableMetadata>> VisibleTablesAsync(
        IReadOnlyList<TableMetadata> allTables,
        ClaimsPrincipal principal,
        TenantId tenantId,
        IConsentRepository? consentRepository,
        Autheris.Application.Governance.Contracts.SchemaContractDefinition? contract,
        CancellationToken ct = default)
        => VisibleTablesAsync(allTables, principal, tenantId, consentRepository, contract, catalogOwner: null, sharedTables: null, ct);

    /// <summary>
    /// Fail-closed default: this overload (and every overload without a surface) is <see cref="CatalogSurface.DataAccess"/>, never
    /// <see cref="CatalogSurface.Discovery"/> (issue #111, pinned by test).
    /// </summary>
    public static Task<IReadOnlyList<TableMetadata>> VisibleTablesAsync(
        IReadOnlyList<TableMetadata> allTables,
        ClaimsPrincipal principal,
        TenantId tenantId,
        IConsentRepository? consentRepository,
        Autheris.Application.Governance.Contracts.SchemaContractDefinition? contract,
        TenantId? catalogOwner,
        IEnumerable<string>? sharedTables,
        CancellationToken ct = default)
        => VisibleTablesAsync(allTables, principal, tenantId, consentRepository, contract, catalogOwner, sharedTables, CatalogSurface.DataAccess, ct);

    /// <summary>Issue #111: as above for an explicit <paramref name="surface"/>; only the discovery call sites pass <see cref="CatalogSurface.Discovery"/>.</summary>
    public static async Task<IReadOnlyList<TableMetadata>> VisibleTablesAsync(
        IReadOnlyList<TableMetadata> allTables,
        ClaimsPrincipal principal,
        TenantId tenantId,
        IConsentRepository? consentRepository,
        Autheris.Application.Governance.Contracts.SchemaContractDefinition? contract,
        TenantId? catalogOwner,
        IEnumerable<string>? sharedTables,
        CatalogSurface surface,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(allTables);
        ArgumentNullException.ThrowIfNull(principal);

        var resolution = CallerScopeResolver.ResolutionOf(principal, catalogOwner);
        var scope = CallerScopeResolver.Resolve(principal, resolution, catalogOwner);
        switch (scope)
        {
            case CallerScope.None:
                return Array.Empty<TableMetadata>();
            case CallerScope.Global:
                return FilterByContract(allTables, contract);
            case CallerScope.CatalogOwner:
                // DT-A-7 / DT-SC-08: the whole physical catalog; the tenant's own virtual tables only (foreign ones dropped, also for the owner).
                return FilterByContract(DropForeignVirtualTables(allTables, resolution.RequireTenant()), contract);
            case CallerScope.Tenant:
            {
                if (consentRepository == null)
                {
                    return Array.Empty<TableMetadata>();
                }

                // Same confinement rule as the GraphQL catalog (consent-derived ownership plus Gateway:Catalog:SharedTables).
                // #112 A1-13 (SEC-112-22/-23): the foreign virtual-table drop runs on the full list first, with the claim tenant.
                var claimTenant = resolution.RequireTenant();
                var ownVirtual = DropForeignVirtualTables(allTables, claimTenant);
                var tenantConsentTables = await consentRepository.GetTablesWithConsentsForTenantAsync(claimTenant, ct).ConfigureAwait(false);
                var confined = FilterToTenant(ownVirtual, claimTenant, tenantConsentTables, sharedTables, catalogOwner);
                return FilterByContract(confined, contract);
            }
        }

        // Subject: the drop uses the tenant the consent query below uses, before any consent is evaluated.
        allTables = DropForeignVirtualTables(allTables, tenantId);

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
        var visible = FilterForSubject(
            allTables, activeConsents, tenantId, Autheris.Domain.Security.TokenAccessScope.GetScopeSet(principal.Claims),
            surface, sharedTables, catalogOwner);
        return FilterByContract(visible, contract);
    }
}
