namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ScimServiceGroupNestingAndCorrelationTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private const string Realm = "workforce-entra";
    private const string ValidGroupSid = "S-1-5-21-1000-2000-3000-2001";
    private const string ValidChildGroupSid = "S-1-5-21-1000-2000-3000-2002";
    private const string ValidUserSid = "S-1-5-21-1000-2000-3000-1001";

    private static (ScimService Service, InMemoryIdentityStore Store) CreateSut()
    {
        var store = new InMemoryIdentityStore();
        var service = new ScimService(store, new NoOpDeprovisioningService());
        return (service, store);
    }

    [Fact]
    public async Task CreateGroupAsync_SplitsUserAndGroupMembersBetweenTables()
    {
        var (service, store) = CreateSut();

        var created = await service.CreateGroupAsync(
            Tenant,
            Realm,
            new ScimGroup(
                "g-parent",
                "ext-parent",
                "Parent",
                [
                    new ScimGroupMember("u-1", "Alice", ScimMemberTypes.User),
                    new ScimGroupMember("g-child", "Child", ScimMemberTypes.Group)
                ],
                null,
                null,
                ObjectSid: ValidGroupSid));

        created.ObjectSid.ShouldBe(ValidGroupSid);
        store.UserMemberships.ShouldContain((Tenant.Value, Realm, "g-parent", "u-1"));
        store.NestingEdges.ShouldContain((Tenant.Value, Realm, "g-parent", "g-child"));

        var loaded = await service.GetGroupAsync(Tenant, Realm, "g-parent");
        loaded.ShouldNotBeNull();
        loaded!.Members.ShouldNotBeNull();
        loaded.Members!.ShouldContain(m => m.Value == "u-1" && m.Type == ScimMemberTypes.User);
        loaded.Members!.ShouldContain(m => m.Value == "g-child" && m.Type == ScimMemberTypes.Group);
    }

    [Fact]
    public async Task CreateGroupAsync_SelfNestingEdge_ThrowsValidationException()
    {
        var (service, _) = CreateSut();

        var group = new ScimGroup(
            "g-self",
            "ext-self",
            "Self",
            [new ScimGroupMember("g-self", "Self", ScimMemberTypes.Group)],
            null,
            null,
            ObjectSid: ValidGroupSid);

        await Should.ThrowAsync<ValidationException>(() => service.CreateGroupAsync(Tenant, Realm, group));
    }

    [Fact]
    public async Task PatchGroupAsync_AddAndRemoveGroupMember_UpdatesNestingStore()
    {
        var (service, store) = CreateSut();

        await service.CreateGroupAsync(
            Tenant, Realm,
            new ScimGroup("g-parent", "ext-p", "Parent", [new ScimGroupMember("g-old", "Old", ScimMemberTypes.Group)], null, null, ValidGroupSid));

        var patchValue = JsonSerializer.SerializeToElement(new[]
        {
            new { value = "g-old", type = "Group" }
        });
        var addValue = JsonSerializer.SerializeToElement(new[]
        {
            new { value = "g-new", type = "Group" },
            new { value = "u-new", type = "User" }
        });

        var patched = await service.PatchGroupAsync(
            Tenant,
            Realm,
            "g-parent",
            new ScimPatchRequest(
                [ScimSchemaUris.PatchOp],
                [
                    new ScimPatchOperation("remove", "members", patchValue),
                    new ScimPatchOperation("add", "members", addValue)
                ]));

        patched.ShouldNotBeNull();
        store.NestingEdges.ShouldNotContain((Tenant.Value, Realm, "g-parent", "g-old"));
        store.NestingEdges.ShouldContain((Tenant.Value, Realm, "g-parent", "g-new"));
        store.UserMemberships.ShouldContain((Tenant.Value, Realm, "g-parent", "u-new"));
    }

    [Fact]
    public async Task CreateUserAsync_NormalizesUpnToLowercaseAndEntraObjectIdToCanonicalGuid()
    {
        var (service, store) = CreateSut();

        var created = await service.CreateUserAsync(
            Tenant,
            Realm,
            new ScimUser(
                "u-1",
                "ext-u1",
                "Alice",
                "Alice",
                "alice@example.com",
                true,
                null,
                null,
                null,
                ObjectSid: ValidUserSid,
                OnPremisesImmutableId: "ImmId123",
                UserPrincipalName: "  Alice.Smith@Corp.EXAMPLE ",
                EntraObjectId: "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"));

        created.UserPrincipalName.ShouldBe("alice.smith@corp.example");
        created.EntraObjectId.ShouldBe("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        store.Users[(Tenant.Value, Realm, "u-1")].UserPrincipalName.ShouldBe("alice.smith@corp.example");
    }

    [Fact]
    public async Task CreateUserOrGroup_DuplicateCorrelationAttribute_PropagatesScimUniquenessConflictException()
    {
        var (service, _) = CreateSut();

        await service.CreateUserAsync(
            Tenant, Realm,
            new ScimUser("u-1", "ext-1", "alice", null, null, true, null, null, null, ObjectSid: ValidUserSid));

        var userEx = await Should.ThrowAsync<ScimUniquenessConflictException>(() => service.CreateUserAsync(
            Tenant, Realm,
            new ScimUser("u-2", "ext-2", "bob", null, null, true, null, null, null, ObjectSid: ValidUserSid)));
        userEx.AttributeName.ShouldBe("objectSid");

        await service.CreateGroupAsync(
            Tenant, Realm,
            new ScimGroup("g-1", "ext-g1", "Group One", null, null, null, ObjectSid: ValidChildGroupSid));

        var groupEx = await Should.ThrowAsync<ScimUniquenessConflictException>(() => service.CreateGroupAsync(
            Tenant, Realm,
            new ScimGroup("g-2", "ext-g2", "Group Two", null, null, null, ObjectSid: ValidChildGroupSid)));
        groupEx.AttributeName.ShouldBe("objectSid");
    }

    [Fact]
    public async Task Mutations_TouchRealmSyncStateLastContactAndLastWrite()
    {
        var (service, store) = CreateSut();

        await service.CreateUserAsync(
            Tenant, Realm,
            new ScimUser("u-1", "ext-1", "alice", null, null, true, null, null, null, ObjectSid: ValidUserSid));

        var state1 = await store.GetRealmSyncStateAsync(Tenant, Realm);
        state1.ShouldNotBeNull();
        state1!.LastContactAt.ShouldNotBeNull();
        state1.LastWriteAt.ShouldNotBeNull();

        await service.CreateGroupAsync(
            Tenant, Realm,
            new ScimGroup("g-1", "ext-g1", "Engineering", null, null, null, ObjectSid: ValidGroupSid));

        var state2 = await store.GetRealmSyncStateAsync(Tenant, Realm);
        state2.ShouldNotBeNull();
        state2!.LastWriteAt!.Value.ShouldBeGreaterThanOrEqualTo(state1.LastWriteAt!.Value);
    }

    private sealed class NoOpDeprovisioningService : IIdentityDeprovisioningService
    {
        public Task DeprovisionAndLockSubjectAsync(TenantId tenantId, string realmId, string userKey, string reason, string source, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class InMemoryIdentityStore : IIdentityStoreRepository
    {
        public Dictionary<(string Tenant, string Realm, string Id), ScimUser> Users { get; } = [];
        public Dictionary<(string Tenant, string Realm, string Id), ScimGroup> Groups { get; } = [];
        public HashSet<(string Tenant, string Realm, string GroupId, string UserKey)> UserMemberships { get; } = [];
        public HashSet<(string Tenant, string Realm, string ParentGroupId, string ChildGroupId)> NestingEdges { get; } = [];
        public Dictionary<(string Tenant, string Realm), ScimRealmSyncState> SyncStates { get; } = [];
        public Dictionary<(string Tenant, string Realm, string UserKey), ScimLock> Locks { get; } = [];

        public Task<ScimUser?> GetUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) =>
            Task.FromResult(Users.GetValueOrDefault((tenantId.Value, realmId, userKey)));

        public Task<ScimUser?> GetUserByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) =>
            Task.FromResult(Users.Values.FirstOrDefault(u => u.ExternalId == externalId));

        public Task<ScimUser?> GetUserByUserNameAsync(TenantId tenantId, string realmId, string userName, CancellationToken ct = default) =>
            Task.FromResult(Users.Values.FirstOrDefault(u => u.UserName == userName));

        private IEnumerable<ScimUser> ScopedUsers(TenantId tenantId, string realmId)
        {
            foreach (var entry in Users)
            {
                if (entry.Key.Tenant == tenantId.Value && entry.Key.Realm == realmId)
                {
                    yield return entry.Value;
                }
            }
        }

        public Task<ScimUser?> GetUserByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default) =>
            Task.FromResult(ScopedUsers(tenantId, realmId).FirstOrDefault(u => u.ObjectSid == objectSid));

        public Task<ScimUser?> GetUserByImmutableIdAsync(TenantId tenantId, string realmId, string onPremisesImmutableId, CancellationToken ct = default) =>
            Task.FromResult(ScopedUsers(tenantId, realmId).FirstOrDefault(u => u.OnPremisesImmutableId == onPremisesImmutableId));

        public Task<ScimUser?> GetUserByEntraObjectIdAsync(TenantId tenantId, string realmId, string entraObjectId, CancellationToken ct = default) =>
            Task.FromResult(ScopedUsers(tenantId, realmId).FirstOrDefault(u => u.EntraObjectId == entraObjectId));

        public Task<ScimUser?> GetUserByUpnAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default)
        {
            var normalized = userPrincipalName.Trim();
            return Task.FromResult(ScopedUsers(tenantId, realmId).FirstOrDefault(u =>
                u.UserPrincipalName is not null &&
                string.Equals(u.UserPrincipalName, normalized, StringComparison.OrdinalIgnoreCase)));
        }

        public Task<IReadOnlyList<ScimUser>> GetUsersByEntraObjectIdOrExternalIdAsync(TenantId tenantId, string realmId, string identifier, CancellationToken ct = default)
        {
            var normalized = identifier.Trim();
            var matches = ScopedUsers(tenantId, realmId).Where(u =>
                string.Equals(u.EntraObjectId, normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(u.ExternalId, normalized, StringComparison.OrdinalIgnoreCase)).ToList();
            return Task.FromResult<IReadOnlyList<ScimUser>>(matches);
        }

        public Task<IReadOnlyList<ScimUser>> GetUsersByUpnOrUserNameAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default)
        {
            var normalized = userPrincipalName.Trim();
            var matches = ScopedUsers(tenantId, realmId).Where(u =>
                string.Equals(u.UserPrincipalName, normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(u.UserName, normalized, StringComparison.OrdinalIgnoreCase)).ToList();
            return Task.FromResult<IReadOnlyList<ScimUser>>(matches);
        }

        public Task UpsertUserAsync(ScimUser user, TenantId tenantId, string realmId, CancellationToken ct = default)
        {
            Users[(tenantId.Value, realmId, user.UserKey)] = user;
            return Task.CompletedTask;
        }

        public Task CreateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default)
        {
            var scoped = Users.Where(kv => kv.Key.Tenant == tenantId.Value && kv.Key.Realm == realmId && kv.Key.Id != user.UserKey).Select(kv => kv.Value).ToList();
            if (user.ObjectSid is not null && scoped.Any(u => u.ObjectSid == user.ObjectSid))
            {
                throw new ScimUniquenessConflictException("objectSid", "Duplicate objectSid.");
            }

            Users[(tenantId.Value, realmId, user.UserKey)] = user;
            return Task.CompletedTask;
        }

        public Task UpdateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default) =>
            CreateUserAsync(tenantId, realmId, user, ct);

        public Task DeactivateUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default)
        {
            Users.Remove((tenantId.Value, realmId, userKey));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ScimUser>> ListUsersAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScimUser>>(Users.Where(kv => kv.Key.Tenant == tenantId.Value && kv.Key.Realm == realmId).Select(kv => kv.Value).ToList());

        public Task<ScimGroup?> GetGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) =>
            Task.FromResult(Groups.GetValueOrDefault((tenantId.Value, realmId, groupId)));

        public Task<ScimGroup?> GetGroupByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) =>
            Task.FromResult(Groups.Values.FirstOrDefault(g => g.ExternalId == externalId));

        public Task<ScimGroup?> GetGroupByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default) =>
            Task.FromResult(Groups.Where(kv => kv.Key.Tenant == tenantId.Value && kv.Key.Realm == realmId).Select(kv => kv.Value).FirstOrDefault(g => g.ObjectSid == objectSid));

        public Task<EffectiveScimGroupsResult> GetEffectiveGroupSidsForUserAsync(TenantId tenantId, string realmId, string userKey, int maxDepth = 32, CancellationToken ct = default) =>
            Task.FromResult(new EffectiveScimGroupsResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false));

        public Task UpsertGroupAsync(ScimGroup group, TenantId tenantId, string realmId, CancellationToken ct = default)
        {
            Groups[(tenantId.Value, realmId, group.GroupId)] = group;
            return Task.CompletedTask;
        }

        public Task CreateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default)
        {
            var scoped = Groups.Where(kv => kv.Key.Tenant == tenantId.Value && kv.Key.Realm == realmId && kv.Key.Id != group.GroupId).Select(kv => kv.Value).ToList();
            if (group.ObjectSid is not null && scoped.Any(g => g.ObjectSid == group.ObjectSid))
            {
                throw new ScimUniquenessConflictException("objectSid", "Duplicate group objectSid.");
            }

            Groups[(tenantId.Value, realmId, group.GroupId)] = group;
            return Task.CompletedTask;
        }

        public Task UpdateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default) =>
            CreateGroupAsync(tenantId, realmId, group, ct);

        public Task<IReadOnlyList<string>> DeleteGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default)
        {
            var members = UserMemberships.Where(m => m.Tenant == tenantId.Value && m.Realm == realmId && m.GroupId == groupId).Select(m => m.UserKey).ToList();
            UserMemberships.RemoveWhere(m => m.Tenant == tenantId.Value && m.Realm == realmId && m.GroupId == groupId);
            NestingEdges.RemoveWhere(e => e.Tenant == tenantId.Value && e.Realm == realmId && (e.ParentGroupId == groupId || e.ChildGroupId == groupId));
            Groups.Remove((tenantId.Value, realmId, groupId));
            return Task.FromResult<IReadOnlyList<string>>(members);
        }

        public Task<IReadOnlyList<ScimGroup>> ListGroupsAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScimGroup>>(Groups.Where(kv => kv.Key.Tenant == tenantId.Value && kv.Key.Realm == realmId).Select(kv => kv.Value).ToList());

        public Task AddGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default)
        {
            UserMemberships.Add((tenantId.Value, realmId, groupId, userKey));
            return Task.CompletedTask;
        }

        public Task RemoveGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default)
        {
            UserMemberships.Remove((tenantId.Value, realmId, groupId, userKey));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetGroupMembershipsForUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(UserMemberships.Where(m => m.Tenant == tenantId.Value && m.Realm == realmId && m.UserKey == userKey).Select(m => m.GroupId).ToList());

        public Task<IReadOnlyList<string>> GetGroupMembersAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(UserMemberships.Where(m => m.Tenant == tenantId.Value && m.Realm == realmId && m.GroupId == groupId).Select(m => m.UserKey).ToList());

        public Task AddGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default)
        {
            NestingEdges.Add((tenantId.Value, realmId, parentGroupId, childGroupId));
            return Task.CompletedTask;
        }

        public Task RemoveGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default)
        {
            NestingEdges.Remove((tenantId.Value, realmId, parentGroupId, childGroupId));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetChildGroupIdsAsync(TenantId tenantId, string realmId, string parentGroupId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(NestingEdges.Where(e => e.Tenant == tenantId.Value && e.Realm == realmId && e.ParentGroupId == parentGroupId).Select(e => e.ChildGroupId).ToList());

        public Task<IReadOnlyList<ScimGroupNestingEdge>> ListGroupNestingEdgesAsync(TenantId tenantId, string realmId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScimGroupNestingEdge>>(NestingEdges.Where(e => e.Tenant == tenantId.Value && e.Realm == realmId).Select(e => new ScimGroupNestingEdge(e.ParentGroupId, e.ChildGroupId)).ToList());

        public Task<ScimRealmSyncState?> GetRealmSyncStateAsync(TenantId tenantId, string realmId, CancellationToken ct = default) =>
            Task.FromResult(SyncStates.GetValueOrDefault((tenantId.Value, realmId)));

        public Task UpsertRealmSyncStateAsync(TenantId tenantId, string realmId, DateTimeOffset lastContactAt, DateTimeOffset? lastWriteAt, CancellationToken ct = default)
        {
            var prev = SyncStates.GetValueOrDefault((tenantId.Value, realmId));
            SyncStates[(tenantId.Value, realmId)] = new ScimRealmSyncState(tenantId, realmId, lastContactAt, lastWriteAt ?? prev?.LastWriteAt, prev?.DeprovisionEpoch ?? 0);
            return Task.CompletedTask;
        }

        public Task<long> IncrementRealmDeprovisionEpochAsync(TenantId tenantId, string realmId, CancellationToken ct = default)
        {
            var prev = SyncStates.GetValueOrDefault((tenantId.Value, realmId));
            var next = (prev?.DeprovisionEpoch ?? 0) + 1;
            SyncStates[(tenantId.Value, realmId)] = new ScimRealmSyncState(tenantId, realmId, prev?.LastContactAt, prev?.LastWriteAt, next);
            return Task.FromResult(next);
        }

        public Task AddLockAsync(ScimLock lockEntry, CancellationToken ct = default) { Locks[(lockEntry.TenantId.Value, lockEntry.RealmId, lockEntry.UserKey)] = lockEntry; return Task.CompletedTask; }
        public Task<bool> IsSubjectLockedAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.FromResult(Locks.ContainsKey((tenantId.Value, realmId, userKey)));
        public Task<IReadOnlyList<ScimLock>> GetLocksAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimLock>>(Locks.Values.ToList());
        public Task<ScimLockWindow> CountLocksSinceAsync(TenantId tenantId, string realmId, IReadOnlyList<string> sources, DateTimeOffset sinceUtc, int cap, CancellationToken ct = default) => Task.FromResult(new ScimLockWindow(0, null));
        public Task RemoveLockAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) { Locks.Remove((tenantId.Value, realmId, userKey)); return Task.CompletedTask; }
    }
}
