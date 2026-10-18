namespace Autheris.Infrastructure.Persistence;

using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public partial class PostgreSqlGovernanceRepository : ICallerAttributeRepository
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

    public async Task<CallerAttributeChangeResult> SubmitDefinitionAsync(CallerAttributeDefinition definition, string actor, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CallerAttributeStore.SubmitDefinitionAsync(conn, definition, actor, at, ct).ConfigureAwait(false);
    }

    public async Task<CallerAttributeChangeResult> RequestDeletionAsync(TenantId tenant, string name, string actor, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CallerAttributeStore.RequestDeletionAsync(conn, tenant, name, actor, at, ct).ConfigureAwait(false);
    }

    public async Task<CallerAttributeChangeResult> ApproveDefinitionAsync(TenantId tenant, string name, string approver, string expectedHash, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CallerAttributeStore.ApproveDefinitionAsync(conn, tenant, name, approver, expectedHash, at, ct).ConfigureAwait(false);
    }

    public async Task<CallerAttributeChangeResult> RejectDefinitionAsync(TenantId tenant, string name, string actor, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CallerAttributeStore.RejectDefinitionAsync(conn, tenant, name, actor, at, ct).ConfigureAwait(false);
    }

    public async Task<CallerAttributeChangeResult> SubmitServiceSetAsync(TenantId tenant, string applicationId, string attributeName, IReadOnlyList<string> values, string actor, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CallerAttributeStore.SubmitServiceSetAsync(conn, tenant, applicationId, attributeName, values, actor, at, ct).ConfigureAwait(false);
    }

    public async Task<CallerAttributeChangeResult> ApproveServiceSetAsync(TenantId tenant, string applicationId, string attributeName, string approver, string expectedHash, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CallerAttributeStore.ApproveServiceSetAsync(conn, tenant, applicationId, attributeName, approver, expectedHash, at, ct).ConfigureAwait(false);
    }

    public async Task<CallerAttributeChangeResult> RejectServiceSetAsync(TenantId tenant, string applicationId, string attributeName, string actor, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CallerAttributeStore.RejectServiceSetAsync(conn, tenant, applicationId, attributeName, actor, at, ct).ConfigureAwait(false);
    }
}
