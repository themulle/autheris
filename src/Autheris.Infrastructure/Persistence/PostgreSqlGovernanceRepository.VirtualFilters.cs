namespace Autheris.Infrastructure.Persistence;

using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

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

    public async Task SaveFilterAsync(VirtualFilter filter, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await VirtualFilterStore.SaveFilterAsync(conn, filter, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteFilterAsync(TenantId tenantId, string name, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await VirtualFilterStore.DeleteFilterAsync(conn, tenantId, name, ct).ConfigureAwait(false);
    }

    public async Task SaveBindingAsync(FilterBinding binding, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await VirtualFilterStore.SaveBindingAsync(conn, binding, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteBindingAsync(TenantId tenantId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await VirtualFilterStore.DeleteBindingAsync(conn, tenantId, id, ct).ConfigureAwait(false);
    }
}
