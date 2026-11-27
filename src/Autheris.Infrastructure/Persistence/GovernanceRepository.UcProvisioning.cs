using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

namespace Autheris.Infrastructure.Persistence;

/// <summary>One provisioning consent event to append: a grant became provisioned or its provisioning failed.</summary>
internal readonly record struct UcProvisioningEventWrite(string EventType, string Status, Guid ConsentId, TableIdentifier Table, DateTimeOffset OccurredAt);

public partial class GovernanceRepository
{
    /// <summary>
    /// Issue #1122: a provisioning feedback write, the routing reads of its events, its audit entries and then its consent events in
    /// one transaction. The tenant consent event lock is the last lock, as in a decision; no read follows the audit chain lock.
    /// </summary>
    internal async Task WriteWithAuditAndConsentEventsAsync(
        Func<DbConnection, DbTransaction, Task> write,
        IReadOnlyList<AuditLogEntry> audit,
        TenantId tenant,
        IReadOnlyList<UcProvisioningEventWrite> events,
        CancellationToken ct)
    {
        TenantScope.RequireTenant(tenant, nameof(GovernanceTable.ConsentEvents));
        StoredTenant.RequireWritable(tenant);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await write(conn, tx).ConfigureAwait(false);

            // Routing reads before the audit chain and the event lock; a consent not found for the tenant rolls everything back.
            var writes = new List<ConsentEventWrite>(events.Count);
            foreach (var e in events)
            {
                writes.Add(await BuildGrantEventWriteAsync(
                    conn, tx, e.EventType, e.Status, e.ConsentId, e.ConsentId, tenant, e.Table, e.OccurredAt, previousGrantId: null, requireRoutingRow: true, ct)
                    .ConfigureAwait(false));
            }

            foreach (var entry in audit)
            {
                await RecordAuditEventAsync(entry, tx, ct).ConfigureAwait(false);
            }

            foreach (var w in writes)
            {
                await AppendConsentEventInTransactionAsync(conn, tx, w, tenant, ct).ConfigureAwait(false);
            }

            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }
}
