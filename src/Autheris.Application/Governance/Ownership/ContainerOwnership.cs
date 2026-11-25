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
/// owner group, owner group to members through the group store. The tenant is the owner tenant of the table (target). A table without an
/// owner tenant belongs to the explicitly configured catalog owner tenant (<c>Tenancy:DefaultTenantId</c>), as discovery scopes it; without
/// a configured catalog owner it has no owner. A malformed or reserved owner tenant owns nothing and never falls back. An identifier without a realm prefix resolves group
/// membership only when exactly one identity realm is configured; otherwise it is never a member (fail closed). Batch reads resolve
/// tables, containers and the actor's groups set-based instead of once per table.
/// </summary>
public sealed class ContainerOwnership(
    ITableMetadataRepository tables,
    IGovernanceDomainRepository domains,
    IScimGroupStore? groupStore = null,
    IIdentityRealmRegistry? realmRegistry = null,
    TenantId? catalogOwnerTenant = null) : IContainerOwnership
{
    private readonly record struct Resolved(TenantId Tenant, ContainerDefaults Container, Sid OwnerGroup);

    public async Task<IReadOnlyList<ContainerOwnerEntry>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var resolved = await ResolveManyAsync([table], ct).ConfigureAwait(false);
        if (!resolved.TryGetValue(table, out var r))
        {
            return [];
        }

        var contacts = new Dictionary<(TenantId, Guid), string?>();
        return [await OwnerEntryAsync(r, contacts, ct).ConfigureAwait(false)];
    }

    public async Task<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>> GetDataOwnersForTablesAsync(
        IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var resolved = await ResolveManyAsync(tables, ct).ConfigureAwait(false);
        return await MapOwnerEntriesAsync(tables, resolved, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>> GetDataOwnersForTableMetadataAsync(
        IReadOnlyCollection<TableMetadata> tables, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var distinctActive = new List<TableMetadata>(tables.Count);
        var seen = new HashSet<TableIdentifier>();
        foreach (var table in tables)
        {
            if (seen.Add(table.Identifier) && table.Table.IsActive)
            {
                distinctActive.Add(table);
            }
        }

        var resolved = new Dictionary<TableIdentifier, Resolved>();
        if (distinctActive.Count > 0)
        {
            try
            {
                await ResolveFromMetadataAsync(distinctActive, resolved, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                resolved.Clear();
            }
        }

        return await MapOwnerEntriesAsync(tables.Select(static t => t.Identifier), resolved, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>> MapOwnerEntriesAsync(
        IEnumerable<TableIdentifier> tableIds,
        IReadOnlyDictionary<TableIdentifier, Resolved> resolved,
        CancellationToken ct)
    {
        var contacts = new Dictionary<(TenantId, Guid), string?>();
        var result = new Dictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>();
        foreach (var table in tableIds)
        {
            if (result.ContainsKey(table))
            {
                continue;
            }

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

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<TableIdentifier, IReadOnlySet<string>>> FilterAuthorizedApproversForTablesAsync(
        IReadOnlyCollection<TableMetadata> requested,
        IReadOnlyCollection<string> candidateApproverSids,
        DateTimeOffset atTime,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(candidateApproverSids);
        var result = new Dictionary<TableIdentifier, IReadOnlySet<string>>();
        var distinctActive = requested.Where(m => result.TryAdd(m.Identifier, System.Collections.Frozen.FrozenSet<string>.Empty) && m.Table.IsActive).ToList();
        var candidates = candidateApproverSids.Where(static s => !string.IsNullOrWhiteSpace(s)).Select(static s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinctActive.Count == 0 || candidates.Count == 0) return result;

        try
        {
            var resolved = new Dictionary<TableIdentifier, Resolved>();
            await ResolveFromMetadataAsync(distinctActive, resolved, ct).ConfigureAwait(false);
            var groupsCache = new Dictionary<(TenantId, string), IReadOnlyCollection<string>>();
            var authCache = new Dictionary<(TenantId, string), IReadOnlySet<string>>();
            foreach (var (table, r) in resolved)
            {
                if (!authCache.TryGetValue((r.Tenant, r.OwnerGroup.Value), out var auth))
                {
                    var matching = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var sid in candidates)
                    {
                        if (SeparationOfDutiesService.IdentifiersOverlap(sid, r.OwnerGroup.Value) ||
                            (await CachedGroupsAsync(groupsCache, r.Tenant, sid, ct).ConfigureAwait(false)).Any(g => SeparationOfDutiesService.IdentifiersOverlap(g, r.OwnerGroup.Value)))
                            matching.Add(sid);
                    }
                    authCache[(r.Tenant, r.OwnerGroup.Value)] = auth = matching;
                }
                result[table] = auth;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var key in result.Keys.ToList()) result[key] = System.Collections.Frozen.FrozenSet<string>.Empty;
        }
        return result;
    }

    private async Task<IReadOnlyCollection<string>> CachedGroupsAsync(Dictionary<(TenantId, string), IReadOnlyCollection<string>> cache, TenantId tenant, string sid, CancellationToken ct) =>
        cache.TryGetValue((tenant, sid), out var cached) ? cached : cache[(tenant, sid)] = await EffectiveGroupsAsync(tenant, sid, ct).ConfigureAwait(false);

    private async Task<ContainerOwnerEntry> OwnerEntryAsync(Resolved r, Dictionary<(TenantId, Guid), string?> contacts, CancellationToken ct)
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
        return new ContainerOwnerEntry
        {
            AdSid = group,
            DisplayName = string.IsNullOrWhiteSpace(contact) ? group.Value : contact!,
            Email = contact is not null && contact.Contains('@', StringComparison.Ordinal) ? contact : string.Empty,
            IsActive = true
        };
    }

    private async Task ResolveFromMetadataAsync(
        IReadOnlyCollection<TableMetadata> distinctActive,
        Dictionary<TableIdentifier, Resolved> target,
        CancellationToken ct)
    {
        var unresolved = distinctActive.Where(static m => m.Table.Id == Guid.Empty).Select(static m => m.Identifier).ToList();
        var fetched = unresolved.Count > 0 ? await tables.GetStructuralTableMetadataBatchAsync(unresolved, ct).ConfigureAwait(false) : [];
        var byId = Enumerable.Range(0, Math.Min(unresolved.Count, fetched.Count))
            .Where(i => fetched[i] is { Table.IsActive: true })
            .ToDictionary(i => unresolved[i], i => fetched[i]!);
        var active = distinctActive
            .Select(m => (Key: m.Identifier, Meta: m.Table.Id != Guid.Empty ? m : byId.GetValueOrDefault(m.Identifier)))
            .Where(static x => x.Meta is not null)
            .Select(static x => (x.Key, x.Meta!))
            .ToList();
        await ResolveActiveAsync(active, target, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the owner of every table set-based: one structural catalog read (ownership needs no dataset catalog overlay), one
    /// container read per tenant. Unresolvable tables are absent.
    /// </summary>
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
            var metas = await tables.GetStructuralTableMetadataBatchAsync(distinct, ct).ConfigureAwait(false);
            var active = new List<(TableIdentifier Key, TableMetadata Meta)>(distinct.Count);
            for (var i = 0; i < distinct.Count && i < metas.Count; i++)
            {
                if (metas[i] is { Table.IsActive: true } meta)
                {
                    active.Add((distinct[i], meta));
                }
            }

            await ResolveActiveAsync(active, result, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A resolution failure never grants ownership.
            result.Clear();
        }

        return result;
    }

    private async Task ResolveActiveAsync(
        IReadOnlyList<(TableIdentifier Key, TableMetadata Meta)> active,
        Dictionary<TableIdentifier, Resolved> target,
        CancellationToken ct)
    {
        var byTenant = new Dictionary<TenantId, List<(TableIdentifier Table, Guid TableId)>>();
        foreach (var (key, meta) in active)
        {
            if (!meta.Table.IsActive || OwnerTenantOf(meta) is not { } tenant)
            {
                continue;
            }

            if (!byTenant.TryGetValue(tenant, out var list))
            {
                byTenant[tenant] = list = [];
            }

            list.Add((key, meta.Table.Id));
        }

        foreach (var (tenant, list) in byTenant)
        {
            var containers = await domains.GetContainersForTablesAsync(tenant, list.Select(static t => t.TableId).Distinct().ToList(), ct).ConfigureAwait(false);
            foreach (var (table, tableId) in list)
            {
                if (containers.TryGetValue(tableId, out var c) && c.IsValid && c.OwnerGroupSid is { } g && !string.IsNullOrWhiteSpace(g.Value))
                {
                    target[table] = new Resolved(tenant, c, g);
                }
            }
        }
    }

    /// <summary>
    /// The owner tenant of the table; a table without one belongs to the configured catalog owner tenant (as discovery scopes it). A
    /// malformed or reserved owner tenant, or a missing one without a configured catalog owner, has no tenant and therefore no owner.
    /// </summary>
    private TenantId? OwnerTenantOf(TableMetadata meta)
    {
        if (string.IsNullOrWhiteSpace(meta.OwnerTenantId))
        {
            return catalogOwnerTenant is { } owner && !string.IsNullOrWhiteSpace(owner.Value) && !TenantId.IsReserved(owner) ? owner : (TenantId?)null;
        }

        return TenantId.TryParse(meta.OwnerTenantId, out var tenant) && !string.IsNullOrWhiteSpace(tenant.Value) && !TenantId.IsReserved(tenant)
            ? (TenantId?)tenant
            : null;
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
