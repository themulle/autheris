namespace Autheris.Infrastructure.Persistence;

using Autheris.Application.VirtualFilters;

public partial class PostgreSqlGovernanceRepository : IVirtualFilterRepository
{
    public async Task<VirtualFilterSnapshot> LoadSnapshotAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await VirtualFilterStore.LoadSnapshotAsync(conn, ct).ConfigureAwait(false);
    }

    public async Task<long> GetGenerationAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await VirtualFilterStore.GetGenerationAsync(conn, ct).ConfigureAwait(false);
    }

    public async Task ApplyAsync(VirtualFilterChangeSet changes, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await VirtualFilterStore.ApplyAsync(conn, changes, ct).ConfigureAwait(false);
    }
}
