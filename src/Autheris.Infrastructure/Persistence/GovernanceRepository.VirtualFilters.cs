namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public partial class GovernanceRepository : IVirtualFilterRepository
{
    public async Task<VirtualFilterSnapshot> LoadSnapshotAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await VirtualFilterStore.LoadSnapshotAsync(conn, ct).ConfigureAwait(false);
    }

    public async Task<long> GetGenerationAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await VirtualFilterStore.GetGenerationAsync(conn, ct).ConfigureAwait(false);
    }

    public Task ApplyAsync(VirtualFilterChangeSet changes, CancellationToken ct = default) =>
        changes.IsEmpty ? Task.CompletedTask : WriteWithAuditAsync(async (c, tx) => { await VirtualFilterStore.ApplyAsync(c, tx, changes, ct).ConfigureAwait(false); return true; }, static _ => true, changes.AuditEntries, ct);

    /// <summary>A write and its audit entries in one transaction: entries follow an applied write and commit with it (fail closed both ways).</summary>
    internal async Task<T> WriteWithAuditAsync<T>(Func<DbConnection, DbTransaction, Task<T>> write, Func<T, bool> applied, IReadOnlyList<AuditLogEntry> audit, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await write(conn, tx).ConfigureAwait(false);
            if (!applied(result))
            {
                await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
                return result;
            }
            foreach (var entry in audit)
            {
                await RecordAuditEventAsync(entry, tx, ct).ConfigureAwait(false);
            }
            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }
}
