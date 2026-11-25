using System;
using Autheris.Application.Catalog.Semantic;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Catalog;

/// <summary>Canonical tenant ownership and target-eligibility checks for catalog tables and virtual objects.</summary>
public static class CatalogTenantOwnership
{
    /// <summary>
    /// Returns true when <paramref name="table"/> belongs to another tenant or carries a malformed or reserved owner tenant.
    /// Physical tables with no explicit <c>OwnerTenantId</c> are not treated as owned by another tenant; foreign semantic virtual
    /// tables always fail closed even when <c>OwnerTenantId</c> is null.
    /// </summary>
    public static bool IsOwnedByAnotherTenant(TableMetadata table, TenantId callerTenant)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (SemanticVirtualTables.IsInSemanticDomain(table.Identifier) &&
            !SemanticVirtualTables.BelongsToTenant(table, callerTenant))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(table.OwnerTenantId))
        {
            return !TenantId.TryParse(table.OwnerTenantId, out var owner) ||
                   TenantId.IsReserved(owner) ||
                   !string.Equals(owner.Value, callerTenant.Value, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>True when <paramref name="meta"/> is non-null, active, visible unless <paramref name="allowDiscoveryHidden"/>, and not owned by another tenant.</summary>
    public static bool IsEligibleTargetInTenant(TableMetadata? meta, TenantId callerTenant, bool allowDiscoveryHidden = false) =>
        meta is not null &&
        meta.Table.IsActive &&
        (allowDiscoveryHidden || !meta.Table.DiscoveryHidden) &&
        !IsOwnedByAnotherTenant(meta, callerTenant);
}
