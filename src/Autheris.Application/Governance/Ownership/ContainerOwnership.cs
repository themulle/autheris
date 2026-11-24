namespace Autheris.Application.Governance.Ownership;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// <see cref="IContainerOwnership"/> on the container model: table to container (<see cref="IGovernanceDomainRepository"/>), container to
/// owner group, owner group to members through the group store. The tenant is the owner tenant of the table (target) only: a table
/// without a valid owner tenant has no owner (no fallback to a default tenant). An identifier without a realm prefix resolves group
/// membership only when exactly one identity realm is configured; otherwise it is never a member (fail closed). Batch reads resolve
/// tables, containers and the actor's groups set-based instead of once per table.
/// </summary>
public sealed class ContainerOwnership(
    ITableMetadataRepository tables,
    IGovernanceDomainRepository domains,
    IScimGroupStore? groupStore = null,
    IIdentityRealmRegistry? realmRegistry = null) : IContainerOwnership
{
    private readonly record struct Resolved(TenantId Tenant, ContainerDefaults Container, Sid OwnerGroup);

    public async Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var resolved = await ResolveManyAsync([table], ct).ConfigureAwait(false);
        if (!resolved.TryGetValue(table, out var r))
        {
            return [];
        }

        var contacts = new Dictionary<(TenantId, Guid), string?>();
        return [await OwnerEntryAsync(r, contacts, ct).ConfigureAwait(false)];
    }

    public async Task<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<DataOwner>>> GetDataOwnersForTablesAsync(
        IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var resolved = await ResolveManyAsync(tables, ct).ConfigureAwait(false);
        var contacts = new Dictionary<(TenantId, Guid), string?>();
        var result = new Dictionary<TableIdentifier, IReadOnlyList<DataOwner>>();
        foreach (var table in tables)
        {
            result[table] = resolved.TryGetValue(table, out var r)
                ? [await OwnerEntryAsync(r, contacts, ct).ConfigureAwait(false)]
                : [];
        }

        return result;
    }

    public async Task<string?> GetContainerKeyForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var resolved = await ResolveManyAsync([table], ct).ConfigureAwait(false);
        return resolved.TryGetValue(table, out var r)
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{r.Container.Container.Kind}:{r.Container.Container.Id}:{r.OwnerGroup.Value}")
            : null;
    }

    public async Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(approverSid.Value))
        {
            return false;
        }

        var owned = await OwnedTablesAsync(approverSid, [table], ct).ConfigureAwait(false);
        return owned.Contains(table);
    }

    /// <remarks>Container ownership has no validity period; <paramref name="atTime"/> does not change the result.</remarks>
    public Task<IReadOnlySet<TableIdentifier>> GetOwnedOrDelegatedTablesAsync(
        Sid actorSid, IReadOnlyCollection<TableIdentifier> tables, DateTimeOffset atTime, CancellationToken ct = default) =>
        OwnedTablesAsync(actorSid, tables, ct);

    private async Task<IReadOnlySet<TableIdentifier>> OwnedTablesAsync(Sid actorSid, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var owned = new HashSet<TableIdentifier>();
        if (string.IsNullOrWhiteSpace(actorSid.Value) || tables.Count == 0)
        {
            return owned;
        }

        var resolved = await ResolveManyAsync(tables, ct).ConfigureAwait(false);
        var groupsByTenant = new Dictionary<TenantId, IReadOnlyCollection<string>>();
        foreach (var (table, r) in resolved)
        {
            if (SeparationOfDutiesService.IdentifiersOverlap(actorSid.Value, r.OwnerGroup.Value))
            {
                owned.Add(table);
                continue;
            }

            if (!groupsByTenant.TryGetValue(r.Tenant, out var groups))
            {
                // One group resolution per tenant, not per table.
                groups = await EffectiveGroupsAsync(r.Tenant, actorSid.Value, ct).ConfigureAwait(false);
                groupsByTenant[r.Tenant] = groups;
            }

            if (groups.Any(g => SeparationOfDutiesService.IdentifiersOverlap(g, r.OwnerGroup.Value)))
            {
                owned.Add(table);
            }
        }

        return owned;
    }

    public async Task<bool> IsAnyApproverAuthorizedForTableAsync(
        TableIdentifier table, IReadOnlyCollection<string> ownerSids, DateTimeOffset atTime, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ownerSids);
        var resolved = await ResolveManyAsync([table], ct).ConfigureAwait(false);
        if (!resolved.TryGetValue(table, out var r))
        {
            return false;
        }

        foreach (var sid in ownerSids)
        {
            if (string.IsNullOrWhiteSpace(sid))
            {
                continue;
            }

            if (SeparationOfDutiesService.IdentifiersOverlap(sid, r.OwnerGroup.Value) ||
                (await EffectiveGroupsAsync(r.Tenant, sid, ct).ConfigureAwait(false)).Any(g => SeparationOfDutiesService.IdentifiersOverlap(g, r.OwnerGroup.Value)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<DataOwner> OwnerEntryAsync(Resolved r, Dictionary<(TenantId, Guid), string?> contacts, CancellationToken ct)
    {
        string? contact = null;
        if (r.Container.Container.Kind == ContainerKind.Domain)
        {
            var key = (r.Tenant, r.Container.Container.Id);
            if (!contacts.TryGetValue(key, out contact))
            {
                try
                {
                    contact = (await domains.GetAsync(r.Tenant, r.Container.Container.Id, ct).ConfigureAwait(false))?.Contact;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    contact = null;
                }

                contacts[key] = contact;
            }
        }

        var group = r.OwnerGroup;
        return new DataOwner
        {
            AdSid = group,
            DisplayName = string.IsNullOrWhiteSpace(contact) ? group.Value : contact!,
            Email = contact is not null && contact.Contains('@', StringComparison.Ordinal) ? contact : string.Empty,
            IsActive = true
        };
    }

    /// <summary>Resolves the owner of every table set-based: one catalog read, one container read per tenant. Unresolvable tables are absent.</summary>
    private async Task<Dictionary<TableIdentifier, Resolved>> ResolveManyAsync(IReadOnlyCollection<TableIdentifier> requested, CancellationToken ct)
    {
        var result = new Dictionary<TableIdentifier, Resolved>();
        var distinct = requested.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return result;
        }

        try
        {
            var metas = await tables.GetTableMetadataBatchAsync(distinct, ct).ConfigureAwait(false);
            var byTenant = new Dictionary<TenantId, List<(TableIdentifier Table, Guid TableId)>>();
            for (var i = 0; i < distinct.Count && i < metas.Count; i++)
            {
                var meta = metas[i];
                if (meta is null || !meta.Table.IsActive)
                {
                    continue;
                }

                // The owner tenant of the table is the only tenant; a missing, malformed or reserved value owns nothing.
                if (!TenantId.TryParse(meta.OwnerTenantId, out var tenant) || string.IsNullOrWhiteSpace(tenant.Value) || TenantId.IsReserved(tenant))
                {
                    continue;
                }

                if (!byTenant.TryGetValue(tenant, out var list))
                {
                    list = [];
                    byTenant[tenant] = list;
                }

                list.Add((distinct[i], meta.Table.Id));
            }

            foreach (var (tenant, list) in byTenant)
            {
                var containers = await domains.GetContainersForTablesAsync(tenant, list.Select(t => t.TableId).Distinct().ToList(), ct).ConfigureAwait(false);
                foreach (var (table, tableId) in list)
                {
                    if (containers.TryGetValue(tableId, out var container) &&
                        container.IsValid &&
                        container.OwnerGroupSid is { } group &&
                        !string.IsNullOrWhiteSpace(group.Value))
                    {
                        result[table] = new Resolved(tenant, container, group);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A resolution failure never grants ownership.
            result.Clear();
        }

        return result;
    }

    /// <summary>The effective groups of <paramref name="identifier"/>; empty when unknown, degraded or not resolvable to exactly one realm.</summary>
    private async Task<IReadOnlyCollection<string>> EffectiveGroupsAsync(TenantId tenant, string identifier, CancellationToken ct)
    {
        if (groupStore is null || !TryResolveRealm(identifier.Trim(), out var realmId, out var key))
        {
            return [];
        }

        try
        {
            var effective = await groupStore.GetEffectiveGroupSidsForUserAsync(tenant, realmId, key, ct: ct).ConfigureAwait(false);
            return effective is null || effective.MaxDepthExceeded || effective.HasNonCanonicalSid
                ? []
                : effective.GroupObjectSids.ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// A realm-qualified identifier (<c>realm|key</c>) names its realm. An unqualified identifier is ambiguous unless exactly one realm is
    /// configured; it then belongs to that realm, otherwise it resolves to no realm (never to an assumed default).
    /// </summary>
    private bool TryResolveRealm(string identifier, out string realmId, out string key)
    {
        realmId = string.Empty;
        key = string.Empty;
        if (identifier.Length == 0)
        {
            return false;
        }

        var sep = identifier.IndexOf('|', StringComparison.Ordinal);
        if (sep >= 0)
        {
            realmId = identifier[..sep].Trim();
            key = identifier[(sep + 1)..].Trim();
            return realmId.Length > 0 && key.Length > 0;
        }

        var realms = realmRegistry?.GetAllRealms();
        if (realms is not { Count: 1 } || string.IsNullOrWhiteSpace(realms[0].Id))
        {
            return false;
        }

        realmId = realms[0].Id;
        key = identifier;
        return true;
    }
}
