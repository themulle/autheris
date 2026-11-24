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

    public Task<CallerAttributeChangeResult> SaveDefinitionAsync(
        CallerAttributeDefinition definition, string actor, DateTimeOffset at, AuditLogEntry? audit = null, CancellationToken ct = default) =>
        WriteWithAuditAsync((c, tx) => CallerAttributeStore.SaveDefinitionAsync(c, tx, definition, actor, at, ct), r => r == CallerAttributeChangeResult.Applied, audit is null ? [] : [audit], ct);

    public Task<CallerAttributeChangeResult> DeleteDefinitionAsync(TenantId tenant, string name, AuditLogEntry? audit = null, CancellationToken ct = default) =>
        WriteWithAuditAsync((c, tx) => CallerAttributeStore.DeleteDefinitionAsync(c, tx, tenant, name, ct), r => r == CallerAttributeChangeResult.Applied, audit is null ? [] : [audit], ct);

    public Task<CallerAttributeChangeResult> SaveServiceSetAsync(
        TenantId tenant, string applicationId, string attributeName, IReadOnlyList<string> values, string actor, DateTimeOffset at,
        AuditLogEntry? audit = null, CancellationToken ct = default) =>
        WriteWithAuditAsync((c, tx) => CallerAttributeStore.SaveServiceSetAsync(c, tx, tenant, applicationId, attributeName, values, actor, at, ct), r => r == CallerAttributeChangeResult.Applied, audit is null ? [] : [audit], ct);
}
