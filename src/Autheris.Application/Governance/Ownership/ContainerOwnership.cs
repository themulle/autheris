namespace Autheris.Application.Governance.Ownership;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Identity;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// <see cref="IContainerOwnership"/> on the container model: table to container (<see cref="IGovernanceDomainRepository"/>), container to
/// owner group, owner group to members through the group store. The tenant is taken from the table (target) and falls back to the
/// catalog owner tenant; without a tenant nothing is owned.
/// </summary>
public sealed class ContainerOwnership(
    ITableMetadataRepository tables,
    IGovernanceDomainRepository domains,
    IScimGroupStore? groupStore = null,
    IIdentityRealmRegistry? realmRegistry = null,
    ITenantResolver? tenantResolver = null) : IContainerOwnership
{
    public async Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(table, ct).ConfigureAwait(false);
        if (resolved is not { } r)
        {
            return [];
        }

        string? contact = null;
        if (r.Container.Container.Kind == ContainerKind.Domain)
        {
            try
            {
                contact = (await domains.GetAsync(r.Tenant, r.Container.Container.Id, ct).ConfigureAwait(false))?.Contact;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                contact = null;
            }
        }

        var group = r.OwnerGroup;
        return
        [
            new DataOwner
            {
                AdSid = group,
                DisplayName = string.IsNullOrWhiteSpace(contact) ? group.Value : contact!,
                Email = contact is not null && contact.Contains('@', StringComparison.Ordinal) ? contact : string.Empty,
                IsActive = true
            }
        ];
    }

    public async Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(approverSid.Value))
        {
            return false;
        }

        var resolved = await ResolveAsync(table, ct).ConfigureAwait(false);
        return resolved is { } r && await IsMemberAsync(r.Tenant, r.OwnerGroup, approverSid.Value, ct).ConfigureAwait(false);
    }

    private async Task<(TenantId Tenant, ContainerDefaults Container, Sid OwnerGroup)?> ResolveAsync(TableIdentifier table, CancellationToken ct)
    {
        try
        {
            var meta = await tables.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
            if (meta is null || !meta.Table.IsActive)
            {
                return null;
            }

            TenantId tenant;
            if (!string.IsNullOrWhiteSpace(meta.OwnerTenantId) && TenantId.TryParse(meta.OwnerTenantId, out var parsed))
            {
                tenant = parsed;
            }
            else if (tenantResolver?.DefaultTenant is { } fallback)
            {
                tenant = fallback;
            }
            else
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(tenant.Value) || TenantId.IsReserved(tenant))
            {
                return null;
            }

            var container = await domains.GetContainerForTableAsync(tenant, meta.Table.Id, ct).ConfigureAwait(false);
            if (!container.IsValid || container.OwnerGroupSid is not { } group || string.IsNullOrWhiteSpace(group.Value))
            {
                return null;
            }

            return (tenant, container, group);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<bool> IsMemberAsync(TenantId tenant, Sid ownerGroup, string identifier, CancellationToken ct)
    {
        if (SeparationOfDutiesService.IdentifiersOverlap(identifier, ownerGroup.Value))
        {
            return true;
        }

        if (groupStore is null)
        {
            return false;
        }

        try
        {
            var trimmed = identifier.Trim();
            var sep = trimmed.IndexOf('|', StringComparison.Ordinal);
            var realmId = sep > 0 ? trimmed[..sep].Trim() : realmRegistry?.GetDefaultRealm()?.Id ?? "default";
            var key = sep > 0 && trimmed.Length > sep + 1 ? trimmed[(sep + 1)..] : trimmed;
            var effective = await groupStore.GetEffectiveGroupSidsForUserAsync(tenant, realmId, key, ct: ct).ConfigureAwait(false);
            return effective is not null &&
                   !effective.MaxDepthExceeded &&
                   !effective.HasNonCanonicalSid &&
                   effective.GroupObjectSids.Any(g => SeparationOfDutiesService.IdentifiersOverlap(g, ownerGroup.Value));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
