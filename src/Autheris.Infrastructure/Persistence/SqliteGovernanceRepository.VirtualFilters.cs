namespace Autheris.Infrastructure.Persistence;

using Autheris.Application.VirtualFilters;

public partial class SqliteGovernanceRepository : IVirtualFilterRepository
{
    public Task<VirtualFilterSnapshot> LoadSnapshotAsync(CancellationToken ct = default) =>
        LockedAsync(() => VirtualFilterStore.LoadSnapshotAsync(_connection, ct), ct);

    public Task<long> GetGenerationAsync(CancellationToken ct = default) =>
        LockedAsync(() => VirtualFilterStore.GetGenerationAsync(_connection, ct), ct);

    public Task ApplyAsync(VirtualFilterChangeSet changes, CancellationToken ct = default) =>
        LockedAsync(async () => { await VirtualFilterStore.ApplyAsync(_connection, changes, ct).ConfigureAwait(false); return true; }, ct);

    private async Task<T> LockedAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }
}
