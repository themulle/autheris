namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Pure tenant-scope and hidden-object visibility rules for authorized discovery.
/// Always drops foreign tenant virtual tables before evaluating ownership, sharing, or consent signals.
/// </summary>
public static class DiscoveryTenantScope
{
    private static readonly IReadOnlySet<TableIdentifier> EmptyTableSet = new HashSet<TableIdentifier>();

    public static bool NeedsTenantConsentLookup(
        IEnumerable<TableMetadata> candidates,
        TenantId tenant,
        TenantId? catalogOwner,
        IReadOnlySet<TableIdentifier> sharedTables)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(sharedTables);

        foreach (var table in candidates)
        {
            if (table is null || !table.Table.IsActive)
            {
                continue;
            }

            if (SemanticVirtualTables.IsInSemanticDomain(table.Identifier) &&
                !SemanticVirtualTables.BelongsToTenant(table, tenant))
            {
                continue;
            }

            if (!IsInScope(table, tenant, catalogOwner, sharedTables, EmptyTableSet))
            {
                return true;
            }
        }

        return false;
    }

    public static DiscoveryArtifacts? SelectPrimaryArtifact(IReadOnlyList<DiscoveryArtifacts>? artifacts)
    {
        if (artifacts is null || artifacts.Count == 0)
        {
            return null;
        }

        return artifacts.FirstOrDefault(static a => a.Provenance == ArtifactProvenance.Upload)
            ?? artifacts.FirstOrDefault(static a => a.Provenance == ArtifactProvenance.Unknown)
            ?? artifacts[0];
    }

    public static IReadOnlyList<TableMetadata> DropForeignVirtualTables(IEnumerable<TableMetadata> tables, TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(tables);
        return tables
            .Where(t => t != null && (!SemanticVirtualTables.IsInSemanticDomain(t.Identifier) || SemanticVirtualTables.BelongsToTenant(t, tenant)))
            .ToList();
    }

    public static HashSet<TableIdentifier> ParseSharedTables(IEnumerable<string>? sharedTables)
    {
        var parsed = new HashSet<TableIdentifier>();
        if (sharedTables is null)
        {
            return parsed;
        }

        foreach (var entry in sharedTables)
        {
            if (!string.IsNullOrWhiteSpace(entry) && TableIdentifier.TryParse(entry, out var id))
            {
                parsed.Add(id);
            }
        }

        return parsed;
    }

    public static bool IsInScope(
        TableMetadata table,
        TenantId tenant,
        TenantId? catalogOwner,
        IReadOnlySet<TableIdentifier> sharedTables,
        IReadOnlySet<TableIdentifier> tenantAllowConsentTables)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(sharedTables);
        ArgumentNullException.ThrowIfNull(tenantAllowConsentTables);

        if (SemanticVirtualTables.IsInSemanticDomain(table.Identifier) &&
            !SemanticVirtualTables.BelongsToTenant(table, tenant))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(table.OwnerTenantId))
        {
            return string.Equals(table.OwnerTenantId, tenant.Value, StringComparison.OrdinalIgnoreCase)
                || tenantAllowConsentTables.Contains(table.Identifier);
        }

        if (CallerScopeResolver.IsOwner(tenant, catalogOwner) || sharedTables.Contains(table.Identifier))
        {
            return true;
        }

        return tenantAllowConsentTables.Contains(table.Identifier);
    }

    /// <summary>
    /// Hidden tables are visible (with <c>IsHidden = true</c>) only to a tenant-scoped GovernanceAdmin,
    /// ClusterAdmin (global), catalog owner admin, or an active data owner / delegate of the table.
    /// Token scopes such as <c>Catalog.ReadAdmin</c> never widen this check.
    /// </summary>
    public static bool CanSeeHiddenObject(
        TableMetadata table,
        CallerScope scope,
        TenantId callerTenant,
        TenantId? catalogOwner,
        IReadOnlySet<TableIdentifier> ownedTables)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(ownedTables);

        if (!table.Table.DiscoveryHidden)
        {
            return true;
        }

        if (scope == CallerScope.Global ||
            scope == CallerScope.Tenant ||
            (scope == CallerScope.CatalogOwner && CallerScopeResolver.IsOwner(callerTenant, catalogOwner)))
        {
            return true;
        }

        return ownedTables.Contains(table.Identifier);
    }
}
