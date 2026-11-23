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

    public async ValueTask<ApproverSet> ResolveAsync(
        TenantId tenant,
        ConsentRequestKind kind,
        PermissionTarget target,
        Sid requester,
        string granteeRef,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(tenant.Value) || TenantId.IsReserved(tenant))
        {
            return new ApproverSet(tenant, kind, null, null, false, GovernanceEligible: false, ItAdminEligible: false);
        }

        var (container, ownerGroupSid) = await ResolveContainerAsync(tenant, target, ct).ConfigureAwait(false);
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

        var a7Fallback = await IsRequesterOrGranteeOwnerMemberAsync(
            tenant, ownerGroupSid!.Value, requester, granteeRef, ct).ConfigureAwait(false);

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
            var realmId = ResolveRealmId(candidate);
            var lookupKey = StripRealmPrefix(candidate, realmId);
            try
            {
                var effective = await _groupStore.GetEffectiveGroupSidsForUserAsync(tenant, realmId, lookupKey, ct: ct).ConfigureAwait(false);
                if (effective is not null && !effective.MaxDepthExceeded && !effective.HasNonCanonicalSid &&
                    effective.GroupObjectSids.Any(g => SeparationOfDutiesService.IdentifiersOverlap(g, ownerGroupSid.Value)))
                {
                    return true;
                }

                var direct = await _groupStore.GetGroupMembershipsForUserAsync(tenant, realmId, lookupKey, ct).ConfigureAwait(false);
                if (direct is not null && direct.Any(g =>
                        SeparationOfDutiesService.IdentifiersOverlap(g, ownerGroupSid.Value) ||
                        (!SidFormat.IsGroupSid(g) && SidFormat.TryFormatRealmGroupKey(realmId, g, out var qualified) &&
                         SeparationOfDutiesService.IdentifiersOverlap(qualified, ownerGroupSid.Value))))
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

    private string ResolveRealmId(string identifier)
    {
        var sep = identifier.IndexOf('|', StringComparison.Ordinal);
        return sep > 0 ? identifier[..sep].Trim() : _realmRegistry?.GetDefaultRealm()?.Id ?? "default";
    }

    private static string StripRealmPrefix(string identifier, string realmId) =>
        identifier.StartsWith($"{realmId}|", StringComparison.OrdinalIgnoreCase) && identifier.Length > realmId.Length + 1
            ? identifier[(realmId.Length + 1)..]
            : identifier;
}
