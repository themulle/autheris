namespace Autheris.Infrastructure.Persistence;

using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public partial class SqliteGovernanceRepository : IVirtualFilterRepository
{
    public Task<VirtualFilterSnapshot> LoadSnapshotAsync(CancellationToken ct = default) =>
        LockedAsync(() => VirtualFilterStore.LoadSnapshotAsync(_connection, ct), ct);

    public Task<long> GetGenerationAsync(CancellationToken ct = default) =>
        LockedAsync(() => VirtualFilterStore.GetGenerationAsync(_connection, ct), ct);

    public Task SaveFilterAsync(VirtualFilter filter, CancellationToken ct = default) =>
        LockedAsync(async () => { await VirtualFilterStore.SaveFilterAsync(_connection, filter, ct).ConfigureAwait(false); return true; }, ct);

    public Task<bool> DeleteFilterAsync(TenantId tenantId, string name, CancellationToken ct = default) =>
        LockedAsync(() => VirtualFilterStore.DeleteFilterAsync(_connection, tenantId, name, ct), ct);

    public Task SaveBindingAsync(FilterBinding binding, CancellationToken ct = default) =>
        LockedAsync(async () => { await VirtualFilterStore.SaveBindingAsync(_connection, binding, ct).ConfigureAwait(false); return true; }, ct);

    public Task<bool> DeleteBindingAsync(TenantId tenantId, Guid id, CancellationToken ct = default) =>
        LockedAsync(() => VirtualFilterStore.DeleteBindingAsync(_connection, tenantId, id, ct), ct);

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
