namespace Autheris.Infrastructure.Persistence;

using Autheris.Application.VirtualFilters;

public partial class SqliteGovernanceRepository : IVirtualFilterRepository
{
    public Task<VirtualFilterSnapshot> LoadSnapshotAsync(CancellationToken ct = default) =>
        ReadAsync(conn => VirtualFilterStore.LoadSnapshotAsync(conn, ct), ct);

    public Task<long> GetGenerationAsync(CancellationToken ct = default) =>
        ReadAsync(conn => VirtualFilterStore.GetGenerationAsync(conn, ct), ct);

    public Task ApplyAsync(VirtualFilterChangeSet changes, CancellationToken ct = default) =>
        WriteAsync(async conn => { await VirtualFilterStore.ApplyAsync(conn, changes, ct).ConfigureAwait(false); return true; }, ct);
}
