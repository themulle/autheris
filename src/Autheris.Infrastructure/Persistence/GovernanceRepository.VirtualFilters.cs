namespace Autheris.Infrastructure.Persistence;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Interfaces;

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

    public async Task ApplyAsync(VirtualFilterChangeSet changes, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await VirtualFilterStore.ApplyAsync(conn, changes, ct).ConfigureAwait(false);
    }
}
