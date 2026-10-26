namespace Autheris.Domain.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;

public interface IIdentityStoreRepository
{
    Task<ScimUser?> GetUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default);
    Task<ScimUser?> GetUserByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default);
    Task UpsertUserAsync(ScimUser user, TenantId tenantId, string realmId, CancellationToken ct = default);
    Task DeactivateUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default);
    Task DeleteUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default);
    Task<IReadOnlyList<ScimUser>> ListUsersAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default);

    Task<ScimGroup?> GetGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default);
    Task<ScimGroup?> GetGroupByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default);
    Task UpsertGroupAsync(ScimGroup group, TenantId tenantId, string realmId, CancellationToken ct = default);
    /// <summary>Deletes the group and its memberships atomically; returns the user keys that were members (for role cache invalidation).</summary>
    Task<IReadOnlyList<string>> DeleteGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default);
    Task<IReadOnlyList<ScimGroup>> ListGroupsAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default);

    Task AddGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default);
    Task RemoveGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetGroupMembershipsForUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetGroupMembersAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default);

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
}
