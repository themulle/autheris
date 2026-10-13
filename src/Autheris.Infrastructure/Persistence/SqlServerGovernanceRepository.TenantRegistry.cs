namespace Autheris.Infrastructure.Persistence;

using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;

public partial class SqlServerGovernanceRepository : ITenantRegistry
{
    public async ValueTask<TenantRegistration> RegisterOrGetAsync(TenantId tenant, string? issuer, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await TenantRegistryStore.RegisterOrGetAsync(conn, tenant, ct).ConfigureAwait(false);
    }

    public async ValueTask<TenantRegistration?> TryGetAsync(TenantId tenant, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await TenantRegistryStore.LookupAsync(conn, tenant, ct).ConfigureAwait(false);
    }

    public async ValueTask<TenantCollisionResolution> ResolveCollisionAsync(TenantId keep, string? expectedCurrent, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await TenantRegistryStore.ResolveCollisionAsync(conn, keep, expectedCurrent, ct).ConfigureAwait(false);
    }

    public async ValueTask PreseedAsync(IEnumerable<string> configuredTenantIds, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await TenantRegistryStore.PreseedAsync(conn, configuredTenantIds, ct).ConfigureAwait(false);
    }
}
