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
    Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default);

    /// <summary>True when <paramref name="approverSid"/> is the owner group or an effective member of it.</summary>
    Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default);

    /// <summary>Batch-loads the owner entries keyed by table identifier.</summary>
    async Task<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<DataOwner>>> GetDataOwnersForTablesAsync(
        IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct = default)
    {
        var result = new Dictionary<TableIdentifier, IReadOnlyList<DataOwner>>();
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
}
