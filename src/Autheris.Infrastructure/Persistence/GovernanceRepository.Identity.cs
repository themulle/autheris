namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Infrastructure.Persistence.Dialects;

public partial class GovernanceRepository : IIdentityStoreRepository
{
    private IdentityStore? _identityStore;

    private IdentityStore IdentityStoreInstance => _identityStore ??= new IdentityStore(
        _dialect is PostgreSqlGovernanceDialect ? IdentityStoreDialect.PostgreSql : IdentityStoreDialect.SqlServer,
        async (_, ct) =>
        {
            var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            return new IdentityStoreScope(connection, connection.DisposeAsync);
        });

    public Task<ScimUser?> GetUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => IdentityStoreInstance.GetUserAsync(tenantId, realmId, userKey, ct);
    public Task<bool> UserKeyExistsInOtherRealmAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => IdentityStoreInstance.UserKeyExistsInOtherRealmAsync(tenantId, realmId, userKey, ct);
    public Task<ScimUser?> GetUserByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) => IdentityStoreInstance.GetUserByExternalIdAsync(tenantId, realmId, externalId, ct);
    public Task<ScimUser?> GetUserByUserNameAsync(TenantId tenantId, string realmId, string userName, CancellationToken ct = default) => IdentityStoreInstance.GetUserByUserNameAsync(tenantId, realmId, userName, ct);
    public Task<ScimUser?> GetUserByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default) => IdentityStoreInstance.GetUserByObjectSidAsync(tenantId, realmId, objectSid, ct);
    public Task<ScimUser?> GetUserByImmutableIdAsync(TenantId tenantId, string realmId, string onPremisesImmutableId, CancellationToken ct = default) => IdentityStoreInstance.GetUserByImmutableIdAsync(tenantId, realmId, onPremisesImmutableId, ct);
    public Task<ScimUser?> GetUserByEntraObjectIdAsync(TenantId tenantId, string realmId, string entraObjectId, CancellationToken ct = default) => IdentityStoreInstance.GetUserByEntraObjectIdAsync(tenantId, realmId, entraObjectId, ct);
    public Task<ScimUser?> GetUserByUpnAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default) => IdentityStoreInstance.GetUserByUpnAsync(tenantId, realmId, userPrincipalName, ct);
    public Task<IReadOnlyList<ScimUser>> GetUsersByEntraObjectIdOrExternalIdAsync(TenantId tenantId, string realmId, string identifier, CancellationToken ct = default) => IdentityStoreInstance.GetUsersByEntraObjectIdOrExternalIdAsync(tenantId, realmId, identifier, ct);
    public Task<IReadOnlyList<ScimUser>> GetUsersByUpnOrUserNameAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default) => IdentityStoreInstance.GetUsersByUpnOrUserNameAsync(tenantId, realmId, userPrincipalName, ct);
    public Task UpsertUserAsync(ScimUser user, TenantId tenantId, string realmId, CancellationToken ct = default) => IdentityStoreInstance.UpsertUserAsync(user, tenantId, realmId, ct);
    public Task CreateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default) => IdentityStoreInstance.CreateUserAsync(tenantId, realmId, user, ct);
    public Task UpdateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default) => IdentityStoreInstance.UpdateUserAsync(tenantId, realmId, user, ct);
    public Task DeactivateUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => IdentityStoreInstance.DeactivateUserAsync(tenantId, realmId, userKey, ct);
    public Task DeleteUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => IdentityStoreInstance.DeleteUserAsync(tenantId, realmId, userKey, ct);
    public Task<IReadOnlyList<ScimUser>> ListUsersAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default) => IdentityStoreInstance.ListUsersAsync(tenantId, realmId, startIndex, count, filter, ct);

    public Task<ScimGroup?> GetGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => IdentityStoreInstance.GetGroupAsync(tenantId, realmId, groupId, ct);
    public Task<ScimGroup?> GetGroupByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) => IdentityStoreInstance.GetGroupByExternalIdAsync(tenantId, realmId, externalId, ct);
    public Task<ScimGroup?> GetGroupByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default) => IdentityStoreInstance.GetGroupByObjectSidAsync(tenantId, realmId, objectSid, ct);
    public Task<string?> GetGroupDisplayNameBySidAsync(TenantId tenant, Sid groupSid, CancellationToken ct = default) => IdentityStoreInstance.GetGroupDisplayNameBySidAsync(tenant, groupSid, ct);
    public Task UpsertGroupAsync(ScimGroup group, TenantId tenantId, string realmId, CancellationToken ct = default) => IdentityStoreInstance.UpsertGroupAsync(group, tenantId, realmId, ct);
    public Task CreateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default) => IdentityStoreInstance.CreateGroupAsync(tenantId, realmId, group, ct);
    public Task UpdateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default) => IdentityStoreInstance.UpdateGroupAsync(tenantId, realmId, group, ct);
    public Task<IReadOnlyList<string>> DeleteGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => IdentityStoreInstance.DeleteGroupAsync(tenantId, realmId, groupId, ct);
    public Task<IReadOnlyList<ScimGroup>> ListGroupsAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default) => IdentityStoreInstance.ListGroupsAsync(tenantId, realmId, startIndex, count, filter, ct);
    public Task AddGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default) => IdentityStoreInstance.AddGroupMemberAsync(tenantId, realmId, groupId, userKey, ct);
    public Task RemoveGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default) => IdentityStoreInstance.RemoveGroupMemberAsync(tenantId, realmId, groupId, userKey, ct);
    public Task<IReadOnlyList<string>> GetGroupMembershipsForUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => IdentityStoreInstance.GetGroupMembershipsForUserAsync(tenantId, realmId, userKey, ct);
    public Task<EffectiveScimGroupsResult> GetEffectiveGroupSidsForUserAsync(TenantId tenantId, string realmId, string userKey, int maxDepth = 32, CancellationToken ct = default) => IdentityStoreInstance.GetEffectiveGroupSidsForUserAsync(tenantId, realmId, userKey, maxDepth, ct);
    public Task<IReadOnlyList<string>> GetGroupMembersAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => IdentityStoreInstance.GetGroupMembersAsync(tenantId, realmId, groupId, ct);
    public Task AddGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default) => IdentityStoreInstance.AddGroupNestingEdgeAsync(tenantId, realmId, parentGroupId, childGroupId, ct);
    public Task RemoveGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default) => IdentityStoreInstance.RemoveGroupNestingEdgeAsync(tenantId, realmId, parentGroupId, childGroupId, ct);
    public Task<IReadOnlyList<string>> GetChildGroupIdsAsync(TenantId tenantId, string realmId, string parentGroupId, CancellationToken ct = default) => IdentityStoreInstance.GetChildGroupIdsAsync(tenantId, realmId, parentGroupId, ct);
    public Task<IReadOnlyList<ScimGroupNestingEdge>> ListGroupNestingEdgesAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => IdentityStoreInstance.ListGroupNestingEdgesAsync(tenantId, realmId, ct);

    public Task AddLockAsync(ScimLock lockEntry, CancellationToken ct = default) => IdentityStoreInstance.AddLockAsync(lockEntry, ct);
    public Task<bool> IsSubjectLockedAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => IdentityStoreInstance.IsSubjectLockedAsync(tenantId, realmId, userKey, ct);
    public Task<IReadOnlyList<ScimLock>> GetLocksAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => IdentityStoreInstance.GetLocksAsync(tenantId, realmId, ct);
    public Task<ScimLockWindow> CountLocksSinceAsync(TenantId tenantId, string realmId, IReadOnlyList<string> sources, DateTimeOffset sinceUtc, int cap, CancellationToken ct = default) => IdentityStoreInstance.CountLocksSinceAsync(tenantId, realmId, sources, sinceUtc, cap, ct);
    public Task RemoveLockAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => IdentityStoreInstance.RemoveLockAsync(tenantId, realmId, userKey, ct);
    public Task<ScimRealmSyncState?> GetRealmSyncStateAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => IdentityStoreInstance.GetRealmSyncStateAsync(tenantId, realmId, ct);
    public Task UpsertRealmSyncStateAsync(TenantId tenantId, string realmId, DateTimeOffset lastContactAt, DateTimeOffset? lastWriteAt, CancellationToken ct = default) => IdentityStoreInstance.UpsertRealmSyncStateAsync(tenantId, realmId, lastContactAt, lastWriteAt, ct);
    public Task<long> IncrementRealmDeprovisionEpochAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => IdentityStoreInstance.IncrementRealmDeprovisionEpochAsync(tenantId, realmId, ct);
}
