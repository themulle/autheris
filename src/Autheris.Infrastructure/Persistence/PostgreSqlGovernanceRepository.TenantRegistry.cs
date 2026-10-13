namespace Autheris.Infrastructure.Persistence;

using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;

public partial class PostgreSqlGovernanceRepository : ITenantRegistry
{
    public async ValueTask<TenantRegistration> RegisterOrGetAsync(TenantId tenant, string? issuer, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await TenantRegistryStore.RegisterOrGetAsync(conn, tenant, ct).ConfigureAwait(false);
    }

    public async ValueTask<TenantRegistration?> TryGetAsync(TenantId tenant, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await TenantRegistryStore.LookupAsync(conn, tenant, ct).ConfigureAwait(false);
    }

    public async ValueTask<bool> ResolveCollisionAsync(TenantId keep, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await TenantRegistryStore.ResolveCollisionAsync(conn, keep, ct).ConfigureAwait(false);
    }

    public async ValueTask PreseedAsync(IEnumerable<string> configuredTenantIds, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await TenantRegistryStore.PreseedAsync(conn, configuredTenantIds, ct).ConfigureAwait(false);
    }
}
