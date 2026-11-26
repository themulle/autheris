namespace Autheris.Application.Governance.Ownership;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Table ownership as defined by the governance container (domain) of a table: the owner group of the container is the only owner
/// authority. Every method fails closed: an unassigned table, a container without owner group, a resolution failure or degraded
/// group data never grants ownership.
/// </summary>
public interface IContainerOwnership
{
    /// <summary>Owner group (and contact) of the container of <paramref name="table"/> as a single owner entry; empty when none.</summary>
    Task<IReadOnlyList<ContainerOwnerEntry>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default);

    /// <summary>
    /// Stable key of the container of <paramref name="table"/> together with its owner group; changes when the table moves to another
    /// container or the owner group changes. Null when the table has no valid container (unassigned or unresolvable). The default derives
    /// the key from the owner group alone.
    /// </summary>
    async Task<string?> GetContainerKeyForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var owners = await GetDataOwnersForTableAsync(table, ct).ConfigureAwait(false);
        var owner = owners.FirstOrDefault(static o => o.IsActive);
        return owner is null || string.IsNullOrWhiteSpace(owner.AdSid.Value) ? null : "owner:" + owner.AdSid.Value;
    }

    /// <summary>True when <paramref name="approverSid"/> is the owner group or an effective member of it.</summary>
    Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default);

    /// <summary>Batch-loads the owner entries keyed by table identifier.</summary>
    async Task<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>> GetDataOwnersForTablesAsync(
        IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct = default)
    {
        var result = new Dictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>();
        foreach (var table in tables)
        {
            var owners = await GetDataOwnersForTableAsync(table, ct).ConfigureAwait(false);
            result[table] = owners.Where(static o => o.IsActive).ToList();
        }

        return result;
    }

    /// <summary>The subset of <paramref name="tables"/> whose container owner group <paramref name="actorSid"/> belongs to.</summary>
    async Task<IReadOnlySet<TableIdentifier>> GetOwnedOrDelegatedTablesAsync(
        Sid actorSid, IReadOnlyCollection<TableIdentifier> tables, DateTimeOffset atTime, CancellationToken ct = default)
    {
        var owned = new HashSet<TableIdentifier>();
        foreach (var table in tables)
        {
            if (await IsAuthorizedApproverForTableAsync(table, actorSid, ct).ConfigureAwait(false))
            {
                owned.Add(table);
            }
        }

        return owned;
    }

    /// <summary>The owner group SID of the container of <paramref name="table"/> (empty set when none).</summary>
    async Task<IReadOnlySet<string>> GetAuthorizedApproverSidsForTableAsync(
        TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default)
    {
        var owners = await GetDataOwnersForTableAsync(table, ct).ConfigureAwait(false);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var owner in owners)
        {
            if (owner.IsActive && !string.IsNullOrWhiteSpace(owner.AdSid.Value))
            {
                set.Add(owner.AdSid.Value);
            }
        }

        return set;
    }

    /// <summary>True when any SID of <paramref name="ownerSids"/> is the owner group or a member of it for <paramref name="table"/>.</summary>
    async Task<bool> IsAnyApproverAuthorizedForTableAsync(
        TableIdentifier table, IReadOnlyCollection<string> ownerSids, DateTimeOffset atTime, CancellationToken ct = default)
    {
        foreach (var sid in ownerSids)
        {
            if (!string.IsNullOrWhiteSpace(sid) &&
                await IsAuthorizedApproverForTableAsync(table, new Sid(sid), ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Batch-loads owner entries from preloaded table metadata, falling back to identifier lookup when needed.</summary>
    Task<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>> GetDataOwnersForTableMetadataAsync(
        IReadOnlyCollection<TableMetadata> tables, CancellationToken ct = default) =>
        GetDataOwnersForTablesAsync(tables.Select(static t => t.Identifier).ToList(), ct);

    /// <summary>Batch-checks which SIDs in <paramref name="candidateApproverSids"/> are authorized container approvers per table (#1063 §4a).</summary>
    async Task<IReadOnlyDictionary<TableIdentifier, IReadOnlySet<string>>> FilterAuthorizedApproversForTablesAsync(
        IReadOnlyCollection<TableMetadata> requested, IReadOnlyCollection<string> candidateApproverSids, DateTimeOffset atTime, CancellationToken ct = default)
    {
        var candidates = candidateApproverSids.Where(static s => !string.IsNullOrWhiteSpace(s)).Select(static s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<TableIdentifier, IReadOnlySet<string>>();
        foreach (var meta in requested)
        {
            if (result.ContainsKey(meta.Identifier)) continue;
            var authorized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (meta.Table.IsActive)
                foreach (var sid in candidates)
                    if (await IsAuthorizedApproverForTableAsync(meta.Identifier, new Sid(sid), ct).ConfigureAwait(false)) authorized.Add(sid);
            result[meta.Identifier] = authorized;
        }
        return result;
    }
}

/// <summary>
/// Optional tenant-scoped resolution implemented by <see cref="ContainerOwnership"/> so tables without an explicit owner tenant
/// resolve their governance domain in the caller's tenant even when no catalog default tenant is configured.
/// </summary>
public interface ITenantScopedContainerOwnership
{
    Task<IReadOnlyList<ContainerOwnerEntry>> GetDataOwnersForTableAsync(TableIdentifier table, TenantId tenant, CancellationToken ct = default);

    Task<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>> GetDataOwnersForTablesAsync(
        IReadOnlyCollection<TableIdentifier> tables, TenantId tenant, CancellationToken ct = default);

    Task<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>> GetDataOwnersForTableMetadataAsync(
        IReadOnlyCollection<TableMetadata> tables, TenantId tenant, CancellationToken ct = default);

    Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, TenantId tenant, CancellationToken ct = default);
}

public static class ContainerOwnershipExtensions
{
    public static Task<IReadOnlyList<ContainerOwnerEntry>> GetDataOwnersForTableInTenantAsync(
        this IContainerOwnership ownership, TableIdentifier table, TenantId tenant, CancellationToken ct = default) =>
        ownership is ITenantScopedContainerOwnership scoped
            ? scoped.GetDataOwnersForTableAsync(table, tenant, ct)
            : ownership.GetDataOwnersForTableAsync(table, ct);

    public static Task<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>> GetDataOwnersForTablesInTenantAsync(
        this IContainerOwnership ownership, IReadOnlyCollection<TableIdentifier> tables, TenantId tenant, CancellationToken ct = default) =>
        ownership is ITenantScopedContainerOwnership scoped
            ? scoped.GetDataOwnersForTablesAsync(tables, tenant, ct)
            : ownership.GetDataOwnersForTablesAsync(tables, ct);

    public static Task<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>> GetDataOwnersForTableMetadataInTenantAsync(
        this IContainerOwnership ownership, IReadOnlyCollection<TableMetadata> tables, TenantId tenant, CancellationToken ct = default) =>
        ownership is ITenantScopedContainerOwnership scoped
            ? scoped.GetDataOwnersForTableMetadataAsync(tables, tenant, ct)
            : ownership.GetDataOwnersForTableMetadataAsync(tables, ct);

    public static Task<bool> IsAuthorizedApproverForTableInTenantAsync(
        this IContainerOwnership ownership, TableIdentifier table, Sid approverSid, TenantId tenant, CancellationToken ct = default) =>
        ownership is ITenantScopedContainerOwnership scoped
            ? scoped.IsAuthorizedApproverForTableAsync(table, approverSid, tenant, ct)
            : ownership.IsAuthorizedApproverForTableAsync(table, approverSid, ct);
}
