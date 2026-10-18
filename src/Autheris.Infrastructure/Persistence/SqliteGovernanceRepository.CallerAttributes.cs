namespace Autheris.Infrastructure.Persistence;

using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public partial class SqliteGovernanceRepository : ICallerAttributeRepository
{
    public async Task<CallerAttributeStoreSnapshot> LoadCallerAttributeSnapshotAsync(CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        return await CallerAttributeStore.LoadSnapshotAsync(scope.Connection, ct, scope.Snapshot).ConfigureAwait(false);
    }

    public Task<long> GetCallerAttributeGenerationAsync(CancellationToken ct = default) =>
        ReadAsync(conn => CallerAttributeStore.GetGenerationAsync(conn, ct), ct);

    public Task<CallerAttributeChangeResult> SubmitDefinitionAsync(CallerAttributeDefinition definition, string actor, DateTimeOffset at, CancellationToken ct = default) =>
        WriteAsync(conn => CallerAttributeStore.SubmitDefinitionAsync(conn, definition, actor, at, ct), ct);

    public Task<CallerAttributeChangeResult> RequestDeletionAsync(TenantId tenant, string name, string actor, DateTimeOffset at, CancellationToken ct = default) =>
        WriteAsync(conn => CallerAttributeStore.RequestDeletionAsync(conn, tenant, name, actor, at, ct), ct);

    public Task<CallerAttributeChangeResult> ApproveDefinitionAsync(TenantId tenant, string name, string approver, string expectedHash, DateTimeOffset at, CancellationToken ct = default) =>
        WriteAsync(conn => CallerAttributeStore.ApproveDefinitionAsync(conn, tenant, name, approver, expectedHash, at, ct), ct);

    public Task<CallerAttributeChangeResult> RejectDefinitionAsync(TenantId tenant, string name, string actor, DateTimeOffset at, CancellationToken ct = default) =>
        WriteAsync(conn => CallerAttributeStore.RejectDefinitionAsync(conn, tenant, name, actor, at, ct), ct);

    public Task<CallerAttributeChangeResult> SubmitServiceSetAsync(TenantId tenant, string applicationId, string attributeName, IReadOnlyList<string> values, string actor, DateTimeOffset at, CancellationToken ct = default) =>
        WriteAsync(conn => CallerAttributeStore.SubmitServiceSetAsync(conn, tenant, applicationId, attributeName, values, actor, at, ct), ct);

    public Task<CallerAttributeChangeResult> ApproveServiceSetAsync(TenantId tenant, string applicationId, string attributeName, string approver, string expectedHash, DateTimeOffset at, CancellationToken ct = default) =>
        WriteAsync(conn => CallerAttributeStore.ApproveServiceSetAsync(conn, tenant, applicationId, attributeName, approver, expectedHash, at, ct), ct);

    public Task<CallerAttributeChangeResult> RejectServiceSetAsync(TenantId tenant, string applicationId, string attributeName, string actor, DateTimeOffset at, CancellationToken ct = default) =>
        WriteAsync(conn => CallerAttributeStore.RejectServiceSetAsync(conn, tenant, applicationId, attributeName, actor, at, ct), ct);
}
