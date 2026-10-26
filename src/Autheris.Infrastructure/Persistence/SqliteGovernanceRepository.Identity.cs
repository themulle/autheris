namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;

public partial class SqliteGovernanceRepository : IIdentityStoreRepository
{
    private IdentityStore? _identityStore;

    private IdentityStore IdentityStoreInstance => _identityStore ??= new IdentityStore(IdentityStoreDialect.Sqlite, async (write, ct) =>
    {
        var scope = write
            ? await WriteScopeAsync(ct).ConfigureAwait(false)
            : await ReadScopeAsync(ct).ConfigureAwait(false);
        return new IdentityStoreScope(scope.Connection, scope.DisposeAsync);
    });

    public Task<ScimUser?> GetUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) =>
        IdentityStoreInstance.GetUserAsync(tenantId, realmId, userKey, ct);

    public Task<ScimUser?> GetUserByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) =>
        IdentityStoreInstance.GetUserByExternalIdAsync(tenantId, realmId, externalId, ct);

    public Task UpsertUserAsync(ScimUser user, TenantId tenantId, string realmId, CancellationToken ct = default) =>
        IdentityStoreInstance.UpsertUserAsync(user, tenantId, realmId, ct);

    public Task DeactivateUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) =>
        IdentityStoreInstance.DeactivateUserAsync(tenantId, realmId, userKey, ct);

    public Task DeleteUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) =>
        IdentityStoreInstance.DeleteUserAsync(tenantId, realmId, userKey, ct);

    public Task<IReadOnlyList<ScimUser>> ListUsersAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default) =>
        IdentityStoreInstance.ListUsersAsync(tenantId, realmId, startIndex, count, filter, ct);

    public Task<ScimGroup?> GetGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) =>
        IdentityStoreInstance.GetGroupAsync(tenantId, realmId, groupId, ct);

    public Task<ScimGroup?> GetGroupByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) =>
        IdentityStoreInstance.GetGroupByExternalIdAsync(tenantId, realmId, externalId, ct);

    public Task UpsertGroupAsync(ScimGroup group, TenantId tenantId, string realmId, CancellationToken ct = default) =>
        IdentityStoreInstance.UpsertGroupAsync(group, tenantId, realmId, ct);

    public Task<IReadOnlyList<string>> DeleteGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) =>
        IdentityStoreInstance.DeleteGroupAsync(tenantId, realmId, groupId, ct);

    public Task<IReadOnlyList<ScimGroup>> ListGroupsAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default) =>
        IdentityStoreInstance.ListGroupsAsync(tenantId, realmId, startIndex, count, filter, ct);

    public Task AddGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default) =>
        IdentityStoreInstance.AddGroupMemberAsync(tenantId, realmId, groupId, userKey, ct);

    public Task RemoveGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default) =>
        IdentityStoreInstance.RemoveGroupMemberAsync(tenantId, realmId, groupId, userKey, ct);

    public Task<IReadOnlyList<string>> GetGroupMembershipsForUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) =>
        IdentityStoreInstance.GetGroupMembershipsForUserAsync(tenantId, realmId, userKey, ct);

    public Task<IReadOnlyList<string>> GetGroupMembersAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) =>
        IdentityStoreInstance.GetGroupMembersAsync(tenantId, realmId, groupId, ct);

    public Task AddLockAsync(ScimLock lockEntry, CancellationToken ct = default) =>
        IdentityStoreInstance.AddLockAsync(lockEntry, ct);

    public Task<bool> IsSubjectLockedAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) =>
        IdentityStoreInstance.IsSubjectLockedAsync(tenantId, realmId, userKey, ct);

    public Task<IReadOnlyList<ScimLock>> GetLocksAsync(TenantId tenantId, string realmId, CancellationToken ct = default) =>
        IdentityStoreInstance.GetLocksAsync(tenantId, realmId, ct);

    public Task<ScimLockWindow> CountLocksSinceAsync(TenantId tenantId, string realmId, IReadOnlyList<string> sources, DateTimeOffset sinceUtc, int cap, CancellationToken ct = default) =>
        IdentityStoreInstance.CountLocksSinceAsync(tenantId, realmId, sources, sinceUtc, cap, ct);

    public Task RemoveLockAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) =>
        IdentityStoreInstance.RemoveLockAsync(tenantId, realmId, userKey, ct);
}
