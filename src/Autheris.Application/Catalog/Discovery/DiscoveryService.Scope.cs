namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;

public sealed partial class DiscoveryService
{
    private async Task<TableMetadata?> FindSingleScopedTableAsync(
        TableIdentifier id,
        CatalogCaller caller,
        Sid sid,
        ScopeMemoKey scopeKey,
        CancellationToken ct)
    {
        lock (_memoLock)
        {
            if (_scopeMemo.TryGetValue(scopeKey, out var cached))
            {
                foreach (var candidate in cached.VisibleTables)
                {
                    if (candidate.Identifier.Equals(id))
                    {
                        return candidate;
                    }
                }

                return null;
            }
        }

        TableMetadata? match = null;
        if (_snapshotProvider is not null)
        {
            var snapshot = await _snapshotProvider.GetSnapshotAsync(ct).ConfigureAwait(false);
            if (snapshot.OrdinalByIdentifier.TryGetValue(id, out var idx))
            {
                match = snapshot.Tables[idx];
            }
        }
        else
        {
            try
            {
                match = await _metadataRepository!.GetTableMetadataAsync(id, ct).ConfigureAwait(false);
            }
            catch (TableMetadataUnavailableException)
            {
                return null;
            }
        }

        if (match is null || !match.Table.IsActive)
        {
            return null;
        }

        if (DiscoveryTenantScope.DropForeignVirtualTables([match], caller.Tenant).Count == 0)
        {
            return null;
        }

        var catalogOwner = TenantResolver.EffectiveDefaultOf(_gatewayOptions.Tenancy);
        var sharedTables = DiscoveryTenantScope.ParseSharedTables(_gatewayOptions.Catalog.SharedTables);
        if (!DiscoveryTenantScope.IsInScope(match, caller.Tenant, catalogOwner, sharedTables, EmptyTableSet))
        {
            var tenantConsentList = await _consentRepository!.GetTablesWithConsentsForTenantAsync(caller.Tenant, ct).ConfigureAwait(false);
            var tenantAllowConsents = tenantConsentList as IReadOnlySet<TableIdentifier> ?? tenantConsentList.ToHashSet();
            if (!DiscoveryTenantScope.IsInScope(match, caller.Tenant, catalogOwner, sharedTables, tenantAllowConsents))
            {
                return null;
            }
        }

        var callerScope = CallerScopeResolver.Resolve(caller.Principal!, catalogOwner);
        IReadOnlySet<TableIdentifier> ownedTables = EmptyTableSet;
        if (match.Table.DiscoveryHidden && _ownershipRepository is not null)
        {
            ownedTables = await _ownershipRepository.GetOwnedOrDelegatedTablesAsync(
                sid, [match.Identifier], _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        }

        return DiscoveryTenantScope.CanSeeHiddenObject(match, callerScope, caller.Tenant, catalogOwner, ownedTables)
            ? match
            : null;
    }

    private Task<ScopedCatalog> LoadScopedCatalogAsync(
        CatalogCaller caller,
        Sid sid,
        ScopeMemoKey scopeKey,
        CancellationToken ct)
        => LoadScopedCatalogAsync(caller, sid, scopeKey, domainFilter: null, schemaFilter: null, ct);

    private async Task<ScopedCatalog> LoadScopedCatalogAsync(
        CatalogCaller caller,
        Sid sid,
        ScopeMemoKey scopeKey,
        string? domainFilter,
        string? schemaFilter,
        CancellationToken ct)
    {
        lock (_memoLock)
        {
            if (_scopeMemo.TryGetValue(scopeKey, out var cached))
            {
                return cached;
            }
        }

        var isUnfilteredQuery = string.IsNullOrWhiteSpace(domainFilter) && string.IsNullOrWhiteSpace(schemaFilter);

        IReadOnlyList<TableMetadata> allTables;
        if (_snapshotProvider is not null)
        {
            var snapshot = await _snapshotProvider.GetSnapshotAsync(ct).ConfigureAwait(false);
            allTables = snapshot.Tables;
        }
        else
        {
            var tableQuery = isUnfilteredQuery
                ? new CatalogTableQuery(IncludeInactive: false)
                : new CatalogTableQuery(
                    DataSource: string.IsNullOrWhiteSpace(domainFilter) ? null : domainFilter.Trim(),
                    Schema: string.IsNullOrWhiteSpace(schemaFilter) ? null : schemaFilter.Trim(),
                    IncludeInactive: false);
            allTables = await CatalogTablePageReader.ReadAllAsync(
                _metadataRepository!, tableQuery, ct: ct).ConfigureAwait(false);
        }

        var virtualFiltered = DiscoveryTenantScope.DropForeignVirtualTables(allTables, caller.Tenant);

        var tenantConsentList = await _consentRepository!.GetTablesWithConsentsForTenantAsync(caller.Tenant, ct).ConfigureAwait(false);
        var tenantAllowConsents = tenantConsentList as IReadOnlySet<TableIdentifier> ?? tenantConsentList.ToHashSet();
        var catalogOwner = TenantResolver.EffectiveDefaultOf(_gatewayOptions.Tenancy);
        var sharedTables = DiscoveryTenantScope.ParseSharedTables(_gatewayOptions.Catalog.SharedTables);

        var inScope = new List<TableMetadata>(virtualFiltered.Count);
        foreach (var table in virtualFiltered)
        {
            if (table.Table.IsActive &&
                DiscoveryTenantScope.IsInScope(table, caller.Tenant, catalogOwner, sharedTables, tenantAllowConsents))
            {
                inScope.Add(table);
            }
        }

        var callerScope = CallerScopeResolver.Resolve(caller.Principal!, catalogOwner);
        var hiddenIds = inScope
            .Where(static t => t.Table.DiscoveryHidden)
            .Select(static t => t.Identifier)
            .ToList();

        IReadOnlySet<TableIdentifier> ownedTables = new HashSet<TableIdentifier>();
        if (hiddenIds.Count > 0 && _ownershipRepository is not null)
        {
            ownedTables = await _ownershipRepository.GetOwnedOrDelegatedTablesAsync(
                sid, hiddenIds, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        }

        var visible = new List<TableMetadata>(inScope.Count);
        foreach (var table in inScope)
        {
            if (DiscoveryTenantScope.CanSeeHiddenObject(table, callerScope, caller.Tenant, catalogOwner, ownedTables))
            {
                visible.Add(table);
            }
        }

        var result = new ScopedCatalog(visible, ownedTables);
        if (_snapshotProvider is not null || isUnfilteredQuery)
        {
            lock (_memoLock)
            {
                _scopeMemo[scopeKey] = result;
            }
        }

        return result;
    }
}
