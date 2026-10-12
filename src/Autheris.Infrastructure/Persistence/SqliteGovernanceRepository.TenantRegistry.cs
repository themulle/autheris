namespace Autheris.Infrastructure.Persistence;

using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;

public partial class SqliteGovernanceRepository : ITenantRegistry
{
    public async ValueTask<TenantRegistration> RegisterOrGetAsync(TenantId tenant, string? issuer, CancellationToken ct) =>
        await LockedAsync(() => TenantRegistryStore.RegisterOrGetAsync(_connection, tenant, ct), ct).ConfigureAwait(false);

    public async ValueTask<TenantRegistration?> TryGetAsync(TenantId tenant, CancellationToken ct) =>
        await LockedAsync(() => TenantRegistryStore.LookupAsync(_connection, tenant, ct), ct).ConfigureAwait(false);

    public async ValueTask PreseedAsync(IEnumerable<string> configuredTenantIds, CancellationToken ct) =>
        await LockedAsync(async () => { await TenantRegistryStore.PreseedAsync(_connection, configuredTenantIds, ct).ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);
}
