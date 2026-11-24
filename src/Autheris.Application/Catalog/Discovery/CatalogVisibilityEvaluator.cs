namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Security;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;

/// <summary>Resolved hidden-object and hidden-column visibility for an interactive or delegated caller.</summary>
public readonly record struct UserHiddenVisibility(
    CallerScope Scope, bool AllHiddenAllowed, IReadOnlySet<TableIdentifier> OwnedHiddenTables)
{
    public bool CanViewHiddenTable(TableMetadata table) => !table.Table.DiscoveryHidden || CanViewHiddenColumns(table);
    public bool CanViewHiddenColumns(TableMetadata table) => AllHiddenAllowed || OwnedHiddenTables.Contains(table.Identifier);
}

/// <summary>Shared catalog tenant-scope, hidden-object visibility, and snapshot-first table loading rules.</summary>
public interface ICatalogVisibilityEvaluator
{
    ValueTask<IReadOnlySet<TableIdentifier>> GetTenantConsentTablesAsync(
        IConsentRepository? consentRepository, TenantId tenantId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TableMetadata>> FilterInScopeAsync(
        IReadOnlyList<TableMetadata> candidates, TenantId tenantId, IConsentRepository? consentRepository, CancellationToken ct = default);
    ValueTask<bool> IsInScopeAsync(
        TableMetadata table, TenantId tenantId, IConsentRepository? consentRepository, CancellationToken ct = default);
    ValueTask<UserHiddenVisibility> EvaluateUserHiddenVisibilityAsync(
        ClaimsPrincipal user, TenantId tenantId, IReadOnlyList<TableMetadata> candidates, bool includeHiddenColumns,
        Autheris.Application.Governance.Ownership.IContainerOwnership? ownershipRepository, CancellationToken ct = default);
    Task<IReadOnlyList<TableMetadata>> LoadActiveTablesAsync(
        ITableMetadataRepository metadataRepository, ICatalogSnapshotProvider? snapshotProvider,
        string? domainFilter = null, string? schemaFilter = null, CancellationToken ct = default);
}

public sealed class CatalogVisibilityEvaluator : ICatalogVisibilityEvaluator
{
    private static readonly IReadOnlySet<TableIdentifier> EmptyTableSet = new HashSet<TableIdentifier>();
    private readonly GatewayOptions _gatewayOptions;
    private readonly ITenantConsentTablesProvider _tenantConsentTables;
    private readonly TimeProvider _timeProvider;

    public CatalogVisibilityEvaluator(
        IOptions<GatewayOptions> gatewayOptions,
        ITenantConsentTablesProvider? tenantConsentTables = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(gatewayOptions);
        _gatewayOptions = gatewayOptions.Value ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _tenantConsentTables = tenantConsentTables ?? new TenantConsentTablesProvider(timeProvider: timeProvider);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<IReadOnlySet<TableIdentifier>> GetTenantConsentTablesAsync(
        IConsentRepository? consentRepository, TenantId tenantId, CancellationToken ct = default) =>
        consentRepository is null ? ValueTask.FromResult(EmptyTableSet)
            : _tenantConsentTables.GetTablesWithConsentsForTenantAsync(consentRepository, tenantId, ct);

    public async ValueTask<IReadOnlyList<TableMetadata>> FilterInScopeAsync(
        IReadOnlyList<TableMetadata> candidates, TenantId tenantId, IConsentRepository? consentRepository, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0) return Array.Empty<TableMetadata>();

        var virtualFiltered = DiscoveryTenantScope.DropForeignVirtualTables(candidates, tenantId);
        var catalogOwner = TenantResolver.EffectiveDefaultOf(_gatewayOptions.Tenancy);
        var sharedTables = DiscoveryTenantScope.ParseSharedTables(_gatewayOptions.Catalog.SharedTables);
        var consentSet = consentRepository is not null &&
            DiscoveryTenantScope.NeedsTenantConsentLookup(virtualFiltered, tenantId, catalogOwner, sharedTables)
            ? await _tenantConsentTables.GetTablesWithConsentsForTenantAsync(consentRepository, tenantId, ct).ConfigureAwait(false)
            : EmptyTableSet;

        var inScope = new List<TableMetadata>(virtualFiltered.Count);
        foreach (var table in virtualFiltered)
        {
            if (table.Table.IsActive && DiscoveryTenantScope.IsInScope(table, tenantId, catalogOwner, sharedTables, consentSet))
                inScope.Add(table);
        }

        return inScope;
    }

    public async ValueTask<bool> IsInScopeAsync(
        TableMetadata table, TenantId tenantId, IConsentRepository? consentRepository, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (!table.Table.IsActive || DiscoveryTenantScope.DropForeignVirtualTables([table], tenantId).Count == 0) return false;

        var catalogOwner = TenantResolver.EffectiveDefaultOf(_gatewayOptions.Tenancy);
        var sharedTables = DiscoveryTenantScope.ParseSharedTables(_gatewayOptions.Catalog.SharedTables);
        if (DiscoveryTenantScope.IsInScope(table, tenantId, catalogOwner, sharedTables, EmptyTableSet)) return true;
        if (consentRepository is null) return false;

        var consentSet = await _tenantConsentTables.GetTablesWithConsentsForTenantAsync(consentRepository, tenantId, ct).ConfigureAwait(false);
        return DiscoveryTenantScope.IsInScope(table, tenantId, catalogOwner, sharedTables, consentSet);
    }

    public async ValueTask<UserHiddenVisibility> EvaluateUserHiddenVisibilityAsync(
        ClaimsPrincipal user, TenantId tenantId, IReadOnlyList<TableMetadata> candidates, bool includeHiddenColumns,
        Autheris.Application.Governance.Ownership.IContainerOwnership? ownershipRepository, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(candidates);

        var catalogOwner = TenantResolver.EffectiveDefaultOf(_gatewayOptions.Tenancy);
        var callerScope = CallerScopeResolver.Resolve(user, catalogOwner);
        var allHiddenAllowed = callerScope is CallerScope.Global or CallerScope.Tenant
            || (callerScope == CallerScope.CatalogOwner && CallerScopeResolver.IsOwner(tenantId, catalogOwner));
        if (allHiddenAllowed || candidates.Count == 0 || ownershipRepository is null || user.GetUserSid() is not { } sid)
            return new UserHiddenVisibility(callerScope, allHiddenAllowed, EmptyTableSet);

        var hiddenIds = candidates
            .Where(t => t.Table.IsActive && (t.Table.DiscoveryHidden || (includeHiddenColumns && t.Columns.Any(static c => c.DiscoveryHidden))))
            .Select(static t => t.Identifier)
            .ToList();
        if (hiddenIds.Count == 0) return new UserHiddenVisibility(callerScope, false, EmptyTableSet);

        var owned = await ownershipRepository.GetOwnedOrDelegatedTablesAsync(sid, hiddenIds, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        return new UserHiddenVisibility(callerScope, false, owned);
    }

    public async Task<IReadOnlyList<TableMetadata>> LoadActiveTablesAsync(
        ITableMetadataRepository metadataRepository, ICatalogSnapshotProvider? snapshotProvider,
        string? domainFilter = null, string? schemaFilter = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadataRepository);
        if (snapshotProvider is null) return await ReadAllTablesFallbackAsync(metadataRepository, domainFilter, schemaFilter, ct).ConfigureAwait(false);

        var snapshot = await snapshotProvider.GetSnapshotAsync(ct).ConfigureAwait(false);
        var d = string.IsNullOrWhiteSpace(domainFilter) ? null : domainFilter.Trim();
        var s = string.IsNullOrWhiteSpace(schemaFilter) ? null : schemaFilter.Trim();
        return snapshot.Tables.Where(t => t.Table.IsActive
            && (d is null || string.Equals(t.Identifier.Domain, d, StringComparison.OrdinalIgnoreCase))
            && (s is null || string.Equals(t.Identifier.Schema, s, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Explicit fallback used only when no <see cref="ICatalogSnapshotProvider"/> is registered in the host.</summary>
    private static Task<IReadOnlyList<TableMetadata>> ReadAllTablesFallbackAsync(
        ITableMetadataRepository metadataRepository, string? domainFilter, string? schemaFilter, CancellationToken ct)
    {
        var query = string.IsNullOrWhiteSpace(domainFilter) && string.IsNullOrWhiteSpace(schemaFilter)
            ? new CatalogTableQuery(IncludeInactive: false)
            : new CatalogTableQuery(
                DataSource: string.IsNullOrWhiteSpace(domainFilter) ? null : domainFilter.Trim(),
                Schema: string.IsNullOrWhiteSpace(schemaFilter) ? null : schemaFilter.Trim(),
                IncludeInactive: false);
        return CatalogTablePageReader.ReadAllAsync(metadataRepository, query, static t => t.Table.IsActive, ct: ct);
    }
}
