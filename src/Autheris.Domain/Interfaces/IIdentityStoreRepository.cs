namespace Autheris.Domain.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;

public interface IScimUserStore
{
    Task<ScimUser?> GetUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default);

    /// <summary>
    /// True when another realm of <paramref name="tenantId"/> holds a user with the same <paramref name="userKey"/>, so a bare
    /// (realm-less) subject form of that key cannot be attributed to <paramref name="realmId"/> alone. The default is
    /// fail-closed: a store that cannot tell reports the key as ambiguous.
    /// </summary>
    Task<bool> UserKeyExistsInOtherRealmAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.FromResult(true);
    Task<ScimUser?> GetUserByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default);
    Task<ScimUser?> GetUserByUserNameAsync(TenantId tenantId, string realmId, string userName, CancellationToken ct = default);
    Task<ScimUser?> GetUserByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default);
    Task<ScimUser?> GetUserByImmutableIdAsync(TenantId tenantId, string realmId, string onPremisesImmutableId, CancellationToken ct = default);
    Task<ScimUser?> GetUserByEntraObjectIdAsync(TenantId tenantId, string realmId, string entraObjectId, CancellationToken ct = default);
    Task<ScimUser?> GetUserByUpnAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default);
    Task<IReadOnlyList<ScimUser>> GetUsersByEntraObjectIdOrExternalIdAsync(TenantId tenantId, string realmId, string identifier, CancellationToken ct = default);
    Task<IReadOnlyList<ScimUser>> GetUsersByUpnOrUserNameAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default);
    Task UpsertUserAsync(ScimUser user, TenantId tenantId, string realmId, CancellationToken ct = default);
    Task CreateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default);
    Task UpdateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default);
    Task DeactivateUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default);
    Task DeleteUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default);
    Task<IReadOnlyList<ScimUser>> ListUsersAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default);
}

public sealed record EffectiveScimGroupsResult(
    IReadOnlySet<string> GroupObjectSids,
    bool MaxDepthExceeded,
    bool HasNonCanonicalSid = false,
    IReadOnlySet<string>? GroupIds = null);

public interface IScimGroupStore
{
    Task<ScimGroup?> GetGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default);
    Task<ScimGroup?> GetGroupByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default);
    Task<ScimGroup?> GetGroupByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default);
    Task<string?> GetGroupDisplayNameBySidAsync(TenantId tenant, Sid groupSid, CancellationToken ct = default) => Task.FromResult<string?>(null);
    Task<EffectiveScimGroupsResult> GetEffectiveGroupSidsForUserAsync(TenantId tenantId, string realmId, string userKey, int maxDepth = 32, CancellationToken ct = default);
    Task UpsertGroupAsync(ScimGroup group, TenantId tenantId, string realmId, CancellationToken ct = default);
    Task CreateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default);
    Task UpdateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default);
    /// <summary>Deletes the group and its memberships atomically; returns the user keys that were members (for role cache invalidation).</summary>
    Task<IReadOnlyList<string>> DeleteGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default);
    Task<IReadOnlyList<ScimGroup>> ListGroupsAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default);

    Task AddGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default);
    Task RemoveGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetGroupMembershipsForUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetGroupMembersAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default);

    Task AddGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default);
    Task RemoveGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetChildGroupIdsAsync(TenantId tenantId, string realmId, string parentGroupId, CancellationToken ct = default);
    Task<IReadOnlyList<ScimGroupNestingEdge>> ListGroupNestingEdgesAsync(TenantId tenantId, string realmId, CancellationToken ct = default);
}

public interface IScimProvisioningStore
{
    Task AddLockAsync(ScimLock lockEntry, CancellationToken ct = default);
    Task<bool> IsSubjectLockedAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default);
    Task<IReadOnlyList<ScimLock>> GetLocksAsync(TenantId tenantId, string realmId, CancellationToken ct = default);
    /// <summary>
    /// Counts the locks of (tenant, realm) whose source is one of <paramref name="sources"/> and whose lock time is at or after
    /// <paramref name="sinceUtc"/>, stopping at <paramref name="cap"/> (bounded scan). Also returns the oldest matching lock time.
    /// One row per user (distinct users).
    /// </summary>
    Task<ScimLockWindow> CountLocksSinceAsync(TenantId tenantId, string realmId, IReadOnlyList<string> sources, DateTimeOffset sinceUtc, int cap, CancellationToken ct = default);
    Task RemoveLockAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default);

    Task<ScimRealmSyncState?> GetRealmSyncStateAsync(TenantId tenantId, string realmId, CancellationToken ct = default);
    Task UpsertRealmSyncStateAsync(TenantId tenantId, string realmId, DateTimeOffset lastContactAt, DateTimeOffset? lastWriteAt, CancellationToken ct = default);
    Task<long> IncrementRealmDeprovisionEpochAsync(TenantId tenantId, string realmId, CancellationToken ct = default);
}

public interface IIdentityStoreRepository : IScimUserStore, IScimGroupStore, IScimProvisioningStore
{
}
