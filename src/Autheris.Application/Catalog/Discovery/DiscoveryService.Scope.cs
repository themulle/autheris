namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Catalog.Visibility;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Protection;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;

public sealed partial class DiscoveryService
{
    private const int DefaultMaxContainerCandidates = 1000;

    private readonly IContainerVisibilitySnapshotProvider? _containerVisibility;
    private readonly IUnmaskGrantResolver? _unmaskResolver;

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

                // The memoized scope holds the complete scoped catalog for the caller.
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

        if (_containerVisibility is null)
        {
            return await FindSingleLegacyAsync(match, caller, ct).ConfigureAwait(false);
        }

        var scoped = await ScopeCandidatesWithContainersAsync([match], caller, sid, ct).ConfigureAwait(false);
        return scoped.VisibleTables.Count > 0 ? scoped.VisibleTables[0] : null;
    }

    private async Task<TableMetadata?> FindSingleLegacyAsync(
        TableMetadata match,
        CatalogCaller caller,
        CancellationToken ct)
    {
        if (!await _visibilityEvaluator.IsInScopeAsync(match, caller.Tenant, _consentRepository, ct).ConfigureAwait(false))
        {
            return null;
        }

        var visibility = await _visibilityEvaluator.EvaluateUserHiddenVisibilityAsync(
            caller.Principal!, caller.Tenant, [match], includeHiddenColumns: false, _ownershipRepository, ct).ConfigureAwait(false);
        return visibility.CanViewHiddenTable(match) ? match : null;
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

        var allTables = await _visibilityEvaluator.LoadActiveTablesAsync(
            _metadataRepository!,
            _snapshotProvider,
            _snapshotProvider is null ? domainFilter : null,
            _snapshotProvider is null ? schemaFilter : null,
            ct).ConfigureAwait(false);

        var virtualFiltered = DiscoveryTenantScope.DropForeignVirtualTables(allTables, caller.Tenant);

        ScopedCatalog result;
        if (_containerVisibility is null)
        {
            result = await LoadScopedLegacyAsync(virtualFiltered, caller, ct).ConfigureAwait(false);
        }
        else
        {
            var active = virtualFiltered.Where(static t => t.Table.IsActive).ToList();
            result = await ScopeCandidatesWithContainersAsync(active, caller, sid, ct).ConfigureAwait(false);
        }

        if (_snapshotProvider is not null || isUnfilteredQuery)
        {
            lock (_memoLock)
            {
                _scopeMemo[scopeKey] = result;
            }
        }

        return result;
    }

    private async Task<ScopedCatalog> LoadScopedLegacyAsync(
        IReadOnlyList<TableMetadata> virtualFiltered,
        CatalogCaller caller,
        CancellationToken ct)
    {
        var inScope = await _visibilityEvaluator.FilterInScopeAsync(
            virtualFiltered, caller.Tenant, _consentRepository, ct).ConfigureAwait(false);
        var visibility = await _visibilityEvaluator.EvaluateUserHiddenVisibilityAsync(
            caller.Principal!, caller.Tenant, inScope, includeHiddenColumns: false, _ownershipRepository, ct).ConfigureAwait(false);

        var visible = new List<TableMetadata>(inScope.Count);
        foreach (var table in inScope)
        {
            if (visibility.CanViewHiddenTable(table))
            {
                visible.Add(table);
            }
        }

        return new ScopedCatalog(visible, visibility.OwnedHiddenTables);
    }

    private Task<ScopedCatalog> ScopeCandidatesWithContainersAsync(
        IReadOnlyList<TableMetadata> candidates,
        CatalogCaller caller,
        Sid sid,
        CancellationToken ct)
    {
        if (candidates.Count == 0 || caller.Principal is null)
        {
            return Task.FromResult(new ScopedCatalog([], EmptyTableSet));
        }

        var catalogOwner = TenantResolver.EffectiveDefaultOf(_gatewayOptions.Tenancy);
        var callerScope = CallerScopeResolver.Resolve(caller.Principal, catalogOwner);
        if (callerScope == CallerScope.None)
        {
            return Task.FromResult(new ScopedCatalog([], EmptyTableSet));
        }

        if (CallerScopeResolver.IsAdminView(callerScope))
        {
            var sharedTables = DiscoveryTenantScope.ParseSharedTables(_gatewayOptions.Catalog.SharedTables);
            return ScopeAdminCandidatesWithContainersAsync(candidates, caller, callerScope, catalogOwner, sharedTables, ct);
        }

        return ScopeSubjectCandidatesWithContainersAsync(candidates, caller, sid, callerScope, catalogOwner, ct);
    }

    private async Task<ScopedCatalog> ScopeAdminCandidatesWithContainersAsync(
        IReadOnlyList<TableMetadata> candidates,
        CatalogCaller caller,
        CallerScope callerScope,
        TenantId? catalogOwner,
        IReadOnlySet<TableIdentifier> sharedTables,
        CancellationToken ct)
    {
        var admitted = new HashSet<TableIdentifier>();
        var remaining = new List<TableMetadata>();

        foreach (var table in candidates)
        {
            if (DiscoveryTenantScope.CanSeeHiddenObject(table, callerScope, caller.Tenant, catalogOwner, EmptyTableSet) &&
                DiscoveryTenantScope.IsInScope(table, caller.Tenant, catalogOwner, sharedTables, EmptyTableSet))
            {
                admitted.Add(table.Identifier);
            }
            else
            {
                remaining.Add(table);
            }
        }

        if (remaining.Count > 0)
        {
            var needsConsentLookup = _consentRepository is not null &&
                remaining.Any(t =>
                    DiscoveryTenantScope.CanSeeHiddenObject(t, callerScope, caller.Tenant, catalogOwner, EmptyTableSet) &&
                    !DiscoveryTenantScope.IsInScope(t, caller.Tenant, catalogOwner, sharedTables, EmptyTableSet));
            var tenantAllowConsents = needsConsentLookup
                ? await _visibilityEvaluator.GetTenantConsentTablesAsync(_consentRepository, caller.Tenant, ct).ConfigureAwait(false)
                : EmptyTableSet;

            var containerCandidates = new List<TableMetadata>();
            foreach (var table in remaining)
            {
                if (!DiscoveryTenantScope.CanSeeHiddenObject(table, callerScope, caller.Tenant, catalogOwner, EmptyTableSet))
                {
                    continue;
                }

                if (DiscoveryTenantScope.IsInScope(table, caller.Tenant, catalogOwner, sharedTables, tenantAllowConsents))
                {
                    admitted.Add(table.Identifier);
                }
                else if (!IsOwnedByAnotherTenant(table, caller.Tenant))
                {
                    containerCandidates.Add(table);
                }
            }

            if (containerCandidates.Count > 0 && _protectionProvider is not null)
            {
                var snapshot = await TryGetContainerSnapshotAsync(caller, ct).ConfigureAwait(false);
                if (snapshot.IsAvailable && HasAnyOwnContainer(snapshot))
                {
                    var bounded = BoundContainerCandidates(containerCandidates);
                    var protections = await ResolveProtectionsAsync(caller.Tenant, bounded, ct).ConfigureAwait(false);
                    foreach (var table in bounded)
                    {
                        if (protections.TryGetValue(table.Identifier, out var prot) &&
                            IsAssignedToOwnContainer(snapshot, prot.Container))
                        {
                            admitted.Add(table.Identifier);
                        }
                    }
                }
            }
        }

        var visible = new List<TableMetadata>(admitted.Count);
        foreach (var table in candidates)
        {
            if (admitted.Contains(table.Identifier))
            {
                visible.Add(table);
            }
        }

        return new ScopedCatalog(visible, EmptyTableSet);
    }

    private async Task<ScopedCatalog> ScopeSubjectCandidatesWithContainersAsync(
        IReadOnlyList<TableMetadata> candidates,
        CatalogCaller caller,
        Sid sid,
        CallerScope callerScope,
        TenantId? catalogOwner,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var principal = caller.Principal!;
        var snapshot = await TryGetContainerSnapshotAsync(caller, ct).ConfigureAwait(false);
        var grantCandidates = await LoadSubjectAllowCandidatesAsync(principal, sid, caller.Tenant, now, ct).ConfigureAwait(false);

        var candidateHasGrant = new bool[candidates.Count];
        var ownershipQueryIds = new List<TableIdentifier>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var table = candidates[i];
            var hasGrant = grantCandidates.IsCandidate(table);
            candidateHasGrant[i] = hasGrant;

            if (table.Table.DiscoveryHidden || (!hasGrant && !IsOwnedByAnotherTenant(table, caller.Tenant)))
            {
                ownershipQueryIds.Add(table.Identifier);
            }
        }

        IReadOnlySet<TableIdentifier> ownedTables = EmptyTableSet;
        if (ownershipQueryIds.Count > 0 && _ownershipRepository is not null)
        {
            ownedTables = await _ownershipRepository.GetOwnedOrDelegatedTablesAsync(
                sid, ownershipQueryIds, now, ct).ConfigureAwait(false);
        }

        var admitted = new HashSet<TableIdentifier>();
        var ungrantedCandidates = new List<TableMetadata>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var table = candidates[i];
            if (!DiscoveryTenantScope.CanSeeHiddenObject(table, callerScope, caller.Tenant, catalogOwner, ownedTables))
            {
                continue;
            }

            if (candidateHasGrant[i] || (!IsOwnedByAnotherTenant(table, caller.Tenant) && ownedTables.Contains(table.Identifier)))
            {
                admitted.Add(table.Identifier);
            }
            else if (!IsOwnedByAnotherTenant(table, caller.Tenant))
            {
                ungrantedCandidates.Add(table);
            }
        }

        if (ungrantedCandidates.Count > 0 &&
            _protectionProvider is not null &&
            snapshot.IsAvailable &&
            CanAddContainerVisibility(snapshot, _unmaskResolver is not null && snapshot.GroupsComplete))
        {
            var bounded = BoundContainerCandidates(ungrantedCandidates);
            var protections = await ResolveProtectionsAsync(caller.Tenant, bounded, ct).ConfigureAwait(false);
            var unmaskSubjects = snapshot.GroupsComplete && !GroupResolutionClaims.IsMarkedDegraded(principal)
                ? new List<Sid>(principal.GetGroupSids()) { sid }
                : [];

            foreach (var table in bounded)
            {
                if (!protections.TryGetValue(table.Identifier, out var protection) ||
                    !IsAssignedToOwnContainer(snapshot, protection.Container))
                {
                    continue;
                }

                if (snapshot.IsTableVisible(protection.Container, table.Identifier) ||
                    await HasUnmaskGrantAsync(caller.Tenant, principal, unmaskSubjects, snapshot, protection, table, ct).ConfigureAwait(false))
                {
                    admitted.Add(table.Identifier);
                }
            }
        }

        var visible = new List<TableMetadata>(admitted.Count);
        foreach (var table in candidates)
        {
            if (admitted.Contains(table.Identifier))
            {
                visible.Add(table);
            }
        }

        return new ScopedCatalog(visible, ownedTables);
    }

    private async Task<ContainerVisibilitySnapshot> TryGetContainerSnapshotAsync(CatalogCaller caller, CancellationToken ct)
    {
        try
        {
            return await _containerVisibility!.GetAsync(caller.Tenant, caller.Principal!, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            RecordFailure(ex, "Discovery container visibility failed closed.");
            return ContainerVisibilitySnapshot.Unavailable();
        }
    }

    private Task<ReadGrantCandidates> LoadSubjectAllowCandidatesAsync(
        System.Security.Claims.ClaimsPrincipal principal,
        Sid sid,
        TenantId tenant,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (_consentRepository is null)
        {
            return Task.FromResult(ReadGrantCandidates.Empty);
        }

        var principalContext = SecurityPrincipalContext.FromPrincipal(principal, tenant, timeProvider: _timeProvider);
        var subjects = principal.GetAllUserSids();
        subjects.Add(sid);
        if (!GroupResolutionClaims.IsMarkedDegraded(principal))
        {
            subjects.UnionWith(principalContext.GroupSids);
        }

        return ReadGrantCandidates.LoadAsync(_consentRepository, subjects, now, tenant, ct, includeWriteOnly: true);
    }

    private async Task<Dictionary<TableIdentifier, EffectiveProtection>> ResolveProtectionsAsync(
        TenantId tenant,
        IReadOnlyList<TableMetadata> candidates,
        CancellationToken ct)
    {
        var result = new Dictionary<TableIdentifier, EffectiveProtection>(candidates.Count);
        IReadOnlyDictionary<TableIdentifier, EffectiveProtection>? batch = null;
        if (candidates.Count > 1)
        {
            try
            {
                batch = await _protectionProvider!.ResolveManyAsync(tenant, candidates, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                RecordFailure(ex, "Discovery container visibility failed closed.");
            }
        }

        foreach (var candidate in candidates)
        {
            if (batch is not null && batch.TryGetValue(candidate.Identifier, out var resolved))
            {
                result[candidate.Identifier] = resolved;
                continue;
            }

            try
            {
                result[candidate.Identifier] = await _protectionProvider!.GetAsync(tenant, candidate.Identifier, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                RecordFailure(ex, "Discovery container visibility failed closed.");
            }
        }

        return result;
    }

    private async ValueTask<bool> HasUnmaskGrantAsync(
        TenantId tenant,
        System.Security.Claims.ClaimsPrincipal principal,
        List<Sid> subjects,
        ContainerVisibilitySnapshot snapshot,
        EffectiveProtection protection,
        TableMetadata row,
        CancellationToken ct)
    {
        if (_unmaskResolver is null || !snapshot.GroupsComplete || GroupResolutionClaims.IsMarkedDegraded(principal) || subjects.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(protection.TableMaskCategory) &&
            !protection.Columns.Values.Any(static c => !string.IsNullOrWhiteSpace(c.EffectiveMaskCategory)))
        {
            return false;
        }

        try
        {
            var coverage = await _unmaskResolver.ResolveAsync(tenant, subjects, snapshot.GroupsComplete, row.Identifier, ct).ConfigureAwait(false);
            return !coverage.IsEmpty;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            RecordFailure(ex, "Discovery unmask grant visibility failed closed.");
            return false;
        }
    }

    private IReadOnlyList<TableMetadata> BoundContainerCandidates(IReadOnlyList<TableMetadata> candidates)
    {
        var bound = Math.Max(0, _gatewayOptions.Governance.AccessPolicy.MaxTablesPerAssignment);
        if (bound == 0)
        {
            bound = DefaultMaxContainerCandidates;
        }

        if (candidates.Count <= bound)
        {
            return candidates;
        }

        _logger.LogWarning(
            "Discovery container visibility evaluated only the first {Bound} of {Count} candidate tables; the rest stay hidden.",
            bound,
            candidates.Count);
        return candidates.Take(bound).ToList();
    }

    private static bool HasAnyOwnContainer(ContainerVisibilitySnapshot snapshot)
    {
        foreach (var info in snapshot.Containers.Values)
        {
            if (!info.IsSystemUnassigned)
            {
                return true;
            }
        }

        return false;
    }

    private static bool CanAddContainerVisibility(ContainerVisibilitySnapshot snapshot, bool hasUnmaskResolver)
    {
        if (!snapshot.IsAvailable || !HasAnyOwnContainer(snapshot))
        {
            return false;
        }

        if (snapshot.SeesEverything || hasUnmaskResolver)
        {
            return true;
        }

        if (snapshot.CatalogGrantedContainers.Count > 0 || snapshot.OwnedContainers.Count > 0 || snapshot.DataGrantedTables.Count > 0)
        {
            return true;
        }

        foreach (var info in snapshot.Containers.Values)
        {
            if (info.Visibility == CatalogVisibilityMode.Open && !info.IsSystemUnassigned)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAssignedToOwnContainer(ContainerVisibilitySnapshot snapshot, ContainerRef container) =>
        snapshot.IsAvailable &&
        snapshot.Containers.TryGetValue(container, out var info) &&
        !info.IsSystemUnassigned;

    private static bool IsOwnedByAnotherTenant(TableMetadata table, TenantId tenant) =>
        !string.IsNullOrWhiteSpace(table.OwnerTenantId) &&
        !string.Equals(table.OwnerTenantId, tenant.Value, StringComparison.OrdinalIgnoreCase);
}
