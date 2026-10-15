namespace Autheris.Infrastructure.Persistence;

using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;

public partial class SqliteGovernanceRepository : ITenantRegistry
{
    public async ValueTask<TenantRegistration> RegisterOrGetAsync(TenantId tenant, string? issuer, CancellationToken ct) =>
        await WriteAsync(conn => TenantRegistryStore.RegisterOrGetAsync(conn, tenant, ct), ct).ConfigureAwait(false);

    public async ValueTask<TenantRegistration?> TryGetAsync(TenantId tenant, CancellationToken ct) =>
        await ReadAsync(conn => TenantRegistryStore.LookupAsync(conn, tenant, ct), ct).ConfigureAwait(false);

    public async ValueTask<TenantCollisionResolution> ResolveCollisionAsync(TenantId keep, string? expectedCurrent, CancellationToken ct) =>
        await WriteAsync(conn => TenantRegistryStore.ResolveCollisionAsync(conn, keep, expectedCurrent, ct), ct).ConfigureAwait(false);

    public async ValueTask PreseedAsync(IEnumerable<string> configuredTenantIds, CancellationToken ct) =>
        await WriteAsync(async conn => { await TenantRegistryStore.PreseedAsync(conn, configuredTenantIds, ct).ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);
}
