namespace Autheris.Infrastructure.Persistence;

using Autheris.Application.VirtualFilters;

public partial class SqliteGovernanceRepository : IVirtualFilterRepository
{
    public async Task<VirtualFilterSnapshot> LoadSnapshotAsync(CancellationToken ct = default)
    {
        // A deferred snapshot: the shared store would otherwise begin an IMMEDIATE (write-locking) transaction outside the write gate.
        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        return await VirtualFilterStore.LoadSnapshotAsync(scope.Connection, ct, scope.Snapshot).ConfigureAwait(false);
    }

    public Task<long> GetGenerationAsync(CancellationToken ct = default) =>
        ReadAsync(conn => VirtualFilterStore.GetGenerationAsync(conn, ct), ct);

    public Task ApplyAsync(VirtualFilterChangeSet changes, CancellationToken ct = default) =>
        WriteAsync(async conn => { await VirtualFilterStore.ApplyAsync(conn, changes, ct).ConfigureAwait(false); return true; }, ct);
}
