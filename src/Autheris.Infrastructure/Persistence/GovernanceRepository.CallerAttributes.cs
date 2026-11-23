namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public partial class GovernanceRepository : ICallerAttributeRepository
{
    public async Task<CallerAttributeStoreSnapshot> LoadCallerAttributeSnapshotAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CallerAttributeStore.LoadSnapshotAsync(conn, ct).ConfigureAwait(false);
    }

    public async Task<long> GetCallerAttributeGenerationAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CallerAttributeStore.GetGenerationAsync(conn, ct).ConfigureAwait(false);
    }

    public async Task<CallerAttributeChangeResult> SaveDefinitionAsync(CallerAttributeDefinition definition, string actor, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CallerAttributeStore.SaveDefinitionAsync(conn, definition, actor, at, ct).ConfigureAwait(false);
    }

    public async Task<CallerAttributeChangeResult> DeleteDefinitionAsync(TenantId tenant, string name, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CallerAttributeStore.DeleteDefinitionAsync(conn, tenant, name, ct).ConfigureAwait(false);
    }

    public async Task<CallerAttributeChangeResult> SaveServiceSetAsync(TenantId tenant, string applicationId, string attributeName, IReadOnlyList<string> values, string actor, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CallerAttributeStore.SaveServiceSetAsync(conn, tenant, applicationId, attributeName, values, actor, at, ct).ConfigureAwait(false);
    }
}
