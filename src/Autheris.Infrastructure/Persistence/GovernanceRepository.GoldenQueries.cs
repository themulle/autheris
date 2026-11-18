namespace Autheris.Infrastructure.Persistence;

using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Infrastructure.Persistence.GoldenQueries;

public partial class GovernanceRepository : IGoldenQueryRepository
{
    public Task ReplaceForTableAsync(TenantId tenant, string tableId, GoldenQuerySource source, IReadOnlyList<GoldenQueryStoreEntry> entries, CancellationToken ct = default)
        => GoldenQueryStoreOperations.ReplaceForTableAsync(this, tenant, tableId, source, entries, ct);

    public Task<IReadOnlyList<GoldenQueryStoreRecord>> GetForTenantAsync(TenantId tenant, CancellationToken ct = default)
        => GoldenQueryStoreOperations.GetForTenantAsync(this, tenant, ct);

    internal Task FlushGoldenScopeAsync(CancellationToken ct) => FlushScopePendingAsync(ct);
}
