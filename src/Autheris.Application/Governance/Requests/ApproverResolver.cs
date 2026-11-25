namespace Autheris.Application.Governance.Requests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Identity;
using Autheris.Application.Products;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Resolved eligible approver groups and capacities for a permission request target.
/// </summary>
public sealed record ApproverSet(
    TenantId Tenant,
    ConsentRequestKind Kind,
    ContainerRef? Container,
    Sid? OwnerGroupSid,
    bool OwnerGroupAssigned,
    bool GovernanceEligible,
    bool ItAdminEligible,
    bool A7GovernanceFallback = false,
    bool RouteInertFallback = false);

/// <summary>
/// Resolves container ownership and eligible approver capacities for a permission request.
/// </summary>
public sealed class ApproverResolver(
    IGovernanceDomainRepository? domainRepository = null,
    IDataProductRepository? productRepository = null,
    IProtectionSnapshotProvider? protectionSnapshotProvider = null,
    ITableMetadataRepository? metadataRepository = null,
    IScimGroupStore? groupStore = null,
    IIdentityRealmRegistry? realmRegistry = null)
{
    private readonly IGovernanceDomainRepository? _domainRepository = domainRepository;
    private readonly IDataProductRepository? _productRepository = productRepository;
    private readonly IProtectionSnapshotProvider? _protectionSnapshotProvider = protectionSnapshotProvider;
    private readonly ITableMetadataRepository? _metadataRepository = metadataRepository;
    private readonly IScimGroupStore? _groupStore = groupStore;
    private readonly IIdentityRealmRegistry? _realmRegistry = realmRegistry;

    public ValueTask<ApproverSet> ResolveAsync(
        TenantId tenant,
        ConsentRequestKind kind,
        PermissionTarget target,
        Sid requester,
        string granteeRef,
        CancellationToken ct = default) =>
        ResolveAsync(tenant, kind, target, requester, granteeRef, cache: null, ct);

    /// <summary>
    /// Resolves the approver set, memoizing container and owner-group membership lookups in <paramref name="cache"/>
    /// so a batch of requests over the same containers resolves each container once.
    /// </summary>
    public async ValueTask<ApproverSet> ResolveAsync(
        TenantId tenant,
        ConsentRequestKind kind,
        PermissionTarget target,
        Sid requester,
        string granteeRef,
        ApproverResolutionCache? cache,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (cache is not null && !string.Equals(cache.Tenant.Value, tenant.Value, StringComparison.Ordinal))
        {
            // A cache belongs to exactly one tenant; never reuse resolutions across tenants.
            cache = null;
        }

        if (string.IsNullOrWhiteSpace(tenant.Value) || TenantId.IsReserved(tenant))
        {
            return new ApproverSet(tenant, kind, null, null, false, GovernanceEligible: false, ItAdminEligible: false);
        }

        var (container, ownerGroupSid) = await ResolveContainerCachedAsync(tenant, target, cache, ct).ConfigureAwait(false);
        var ownerGroupAssigned = ownerGroupSid is { } og && !string.IsNullOrWhiteSpace(og.Value);

        if (kind == ConsentRequestKind.Catalog)
        {
            return new ApproverSet(
                tenant,
                kind,
                container,
                ownerGroupSid,
                ownerGroupAssigned,
                GovernanceEligible: true,
                ItAdminEligible: true);
        }

        if (kind == ConsentRequestKind.ProfileAssignment)
        {
            return new ApproverSet(
                tenant,
                kind,
                container,
                ownerGroupSid,
                ownerGroupAssigned,
                GovernanceEligible: true,
                ItAdminEligible: false);
        }

        if (!ownerGroupAssigned)
        {
            return new ApproverSet(
                tenant,
                kind,
                container,
                OwnerGroupSid: null,
                OwnerGroupAssigned: false,
                GovernanceEligible: true,
                ItAdminEligible: true);
        }

        bool a7Fallback;
        var a7Key = (ownerGroupSid!.Value.Value ?? string.Empty, requester.Value ?? string.Empty, granteeRef ?? string.Empty);
        if (cache is not null && cache.OwnerMembership.TryGetValue(a7Key, out var memo))
        {
            a7Fallback = memo;
        }
        else
        {
            a7Fallback = await IsRequesterOrGranteeOwnerMemberAsync(
                tenant, ownerGroupSid.Value, requester, granteeRef ?? string.Empty, ct).ConfigureAwait(false);
            cache?.OwnerMembership.TryAdd(a7Key, a7Fallback);
        }

        return new ApproverSet(
            tenant,
            kind,
            container,
            ownerGroupSid,
            OwnerGroupAssigned: true,
            GovernanceEligible: a7Fallback,
            ItAdminEligible: false,
            A7GovernanceFallback: a7Fallback);
    }

    private async ValueTask<(ContainerRef? Container, Sid? OwnerGroupSid)> ResolveContainerCachedAsync(
        TenantId tenant,
        PermissionTarget target,
        ApproverResolutionCache? cache,
        CancellationToken ct)
    {
        if (cache is null)
        {
            return await ResolveContainerAsync(tenant, target, ct).ConfigureAwait(false);
        }

        if (target.HasContainer)
        {
            var key = new ContainerRef(target.ContainerKind!.Value, target.ContainerId!.Value);
            if (!cache.Containers.TryGetValue(key, out var byContainer))
            {
                byContainer = await ResolveContainerAsync(tenant, target, ct).ConfigureAwait(false);
                cache.Containers[key] = byContainer;
            }

            return byContainer;
        }

        if (target.HasTable)
        {
            var key = target.Table!.Value;
            if (!cache.Tables.TryGetValue(key, out var byTable))
            {
                byTable = await ResolveContainerAsync(tenant, target, ct).ConfigureAwait(false);
                cache.Tables[key] = byTable;
            }

            return byTable;
        }

        return await ResolveContainerAsync(tenant, target, ct).ConfigureAwait(false);
    }

    private async ValueTask<(ContainerRef? Container, Sid? OwnerGroupSid)> ResolveContainerAsync(
        TenantId tenant,
        PermissionTarget target,
        CancellationToken ct)
    {
        if (target.HasContainer)
        {
            var containerRef = new ContainerRef(target.ContainerKind!.Value, target.ContainerId!.Value);
            if (containerRef.Kind == ContainerKind.Domain && _domainRepository is not null)
            {
                var domain = await _domainRepository.GetAsync(tenant, containerRef.Id, ct).ConfigureAwait(false);
                return domain is null ? (containerRef, null) : (containerRef, domain.OwnerGroupSid);
            }

            if (containerRef.Kind == ContainerKind.DataProduct && _productRepository is not null)
            {
                var product = await _productRepository.GetProductByIdAsync(tenant, containerRef.Id, ct).ConfigureAwait(false);
                return product is null ? (containerRef, null) : (containerRef, product.OwnerGroupSid);
            }

            return (containerRef, null);
        }

        if (target.HasTable)
        {
            if (_protectionSnapshotProvider is not null)
            {
                var snapshot = await _protectionSnapshotProvider.GetAsync(tenant, target.Table!.Value, ct).ConfigureAwait(false);
                return (snapshot.Container, snapshot.OwnerGroupSid);
            }

            if (_metadataRepository is not null && _domainRepository is not null)
            {
                var meta = await _metadataRepository.GetTableMetadataAsync(target.Table!.Value, ct).ConfigureAwait(false);
                if (meta is not null)
                {
                    var defaults = await _domainRepository.GetContainerForTableAsync(tenant, meta.Table.Id, ct).ConfigureAwait(false);
                    return (defaults.Container, defaults.OwnerGroupSid);
                }
            }
        }

        return (null, null);
    }

    private async ValueTask<bool> IsRequesterOrGranteeOwnerMemberAsync(
        TenantId tenant,
        Sid ownerGroupSid,
        Sid requester,
        string granteeRef,
        CancellationToken ct)
    {
        if (SeparationOfDutiesService.IdentifiersOverlap(requester.Value, ownerGroupSid.Value) ||
            SeparationOfDutiesService.IdentifiersOverlap(granteeRef, ownerGroupSid.Value))
        {
            return true;
        }

        if (_groupStore is null)
        {
            return false;
        }

        var candidates = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(requester.Value))
        {
            candidates.Add(requester.Value.Trim());
        }

        if (!string.IsNullOrWhiteSpace(granteeRef))
        {
            candidates.Add(granteeRef.Trim());
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryResolveRealmAndKey(candidate, out var realm, out var realmId, out var lookupKey))
            {
                continue;
            }

            try
            {
                var effective = await _groupStore.GetEffectiveGroupSidsForUserAsync(tenant, realmId, lookupKey, ct: ct).ConfigureAwait(false);
                if (effective is not null && !effective.MaxDepthExceeded && !effective.HasNonCanonicalSid)
                {
                    if (effective.GroupObjectSids.Any(g =>
                            IsGroupValidForRealm(g, realm, realmId) &&
                            SeparationOfDutiesService.IdentifiersOverlap(g, ownerGroupSid.Value)))
                    {
                        return true;
                    }

                    if (effective.GroupIds is not null && effective.GroupIds.Any(id =>
                            SidFormat.TryFormatRealmGroupKey(realmId, id, out var qualified) &&
                            IsGroupValidForRealm(qualified, realm, realmId) &&
                            SeparationOfDutiesService.IdentifiersOverlap(qualified, ownerGroupSid.Value)))
                    {
                        return true;
                    }
                }

                var direct = await _groupStore.GetGroupMembershipsForUserAsync(tenant, realmId, lookupKey, ct).ConfigureAwait(false);
                if (direct is not null && direct.Any(g => IsValidDirectGroupMatch(g, realm, realmId, ownerGroupSid.Value)))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Group lookup failures never assume membership; stay fail-closed.
            }
        }

        return false;
    }

    private static bool IsGroupValidForRealm(string rawGroup, IdentityRealm? realm, string realmId) =>
        realm is not null
            ? SidFormat.IsValidGroupIdentifierForRealm(rawGroup, realm.Kind, realmId, SidFormat.HasTenantBoundEntraIssuers(realm))
            : !SidFormat.IsRealmGroupKey(rawGroup) || SidFormat.MatchesRealmGroupKey(rawGroup, realmId);

    private static bool IsValidDirectGroupMatch(
        string rawGroup, IdentityRealm? realm, string realmId, string ownerGroupSid) =>
        (IsGroupValidForRealm(rawGroup, realm, realmId) &&
         SeparationOfDutiesService.IdentifiersOverlap(rawGroup, ownerGroupSid)) ||
        (!SidFormat.IsGroupSid(rawGroup) &&
         SidFormat.TryFormatRealmGroupKey(realmId, rawGroup, out var qualified) &&
         IsGroupValidForRealm(qualified, realm, realmId) &&
         SeparationOfDutiesService.IdentifiersOverlap(qualified, ownerGroupSid));

    private bool TryResolveRealmAndKey(string identifier, out IdentityRealm? realm, out string realmId, out string lookupKey)
    {
        realm = null;
        realmId = string.Empty;
        lookupKey = string.Empty;

        var trimmed = identifier.Trim();
        var sep = trimmed.IndexOf('|', StringComparison.Ordinal);
        if (sep > 0)
        {
            var prefix = trimmed[..sep].Trim();
            var bare = trimmed[(sep + 1)..].Trim();
            if (prefix.Length == 0 || bare.Length == 0) return false;
            if (_realmRegistry is not null)
            {
                if (!_realmRegistry.TryGetRealm(prefix, out var resolved) || resolved is null) return false;
                realm = resolved;
                realmId = resolved.Id;
            }
            else
            {
                realmId = prefix;
            }

            lookupKey = bare;
            return true;
        }

        if (trimmed.Length == 0) return false;
        lookupKey = trimmed;
        if (_realmRegistry is null)
        {
            realmId = "default";
            return true;
        }

        // Unqualified identifiers are ambiguous when more or fewer than one realm exist; never guess or fall back to a default realm.
        var realms = _realmRegistry.GetAllRealms();
        if (realms.Count != 1) return false;
        realm = realms[0];
        realmId = realm.Id;
        return true;
    }
}

/// <summary>
/// Memo of container and owner-group membership resolutions for one tenant within one caller operation (for example one
/// inbox page). It is never shared across callers or requests, so it cannot serve stale ownership beyond that operation.
/// </summary>
public sealed class ApproverResolutionCache(TenantId tenant)
{
    public TenantId Tenant { get; } = tenant;

    internal Dictionary<ContainerRef, (ContainerRef? Container, Sid? OwnerGroupSid)> Containers { get; } = new();

    internal Dictionary<TableIdentifier, (ContainerRef? Container, Sid? OwnerGroupSid)> Tables { get; } = new();

    internal Dictionary<(string OwnerGroup, string Requester, string Grantee), bool> OwnerMembership { get; } = new();
}
