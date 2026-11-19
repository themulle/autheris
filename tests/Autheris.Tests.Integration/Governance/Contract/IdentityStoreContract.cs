using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>IIdentityStoreRepository</c> (SCIM users, groups, memberships, nesting, locks and realm sync state) across PostgreSQL and
/// SQL Server engines. Every row is keyed by tenant and realm first; deprovisioning steps bump the realm deprovision epoch.
/// </summary>
public abstract class IdentityStoreContract : GovernanceContractBase
{
    private const string Realm = "realm-1";
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    private IIdentityStoreRepository Repo => Store.IdentityStore;

    private static string Id(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private static ScimUser User(string key, string? externalId = null, bool active = true, DateTimeOffset? created = null) =>
        new(key, externalId ?? "ext-" + key, key + "@example.com", "Display " + key, key + "@example.com", active, active ? null : Now, created ?? Now, created ?? Now);

    private static ScimGroup Group(string key, string name = "Group", string? externalId = null) =>
        new(key, externalId ?? "ext-" + key, name, null, Now, Now);

    private async Task<(string User, string Group)> UserInGroupAsync(TenantId tenant)
    {
        var user = Id("u");
        var group = Id("g");
        await Repo.UpsertUserAsync(User(user), tenant, Realm);
        await Repo.UpsertGroupAsync(Group(group), tenant, Realm);
        await Repo.AddGroupMemberAsync(tenant, Realm, group, user);
        return (user, group);
    }

    private async Task<long> EpochAsync(TenantId tenant) =>
        (await Repo.GetRealmSyncStateAsync(tenant, Realm))?.DeprovisionEpoch ?? 0;

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var (user, group) = await UserInGroupAsync(Store.TenantA);
        await Repo.AddLockAsync(new ScimLock(Store.TenantA, Realm, user, "reason", "SCIM_DELETE", Now));
        await Repo.AddGroupNestingEdgeAsync(Store.TenantA, Realm, group, Id("child"));
        await Repo.UpsertRealmSyncStateAsync(Store.TenantA, Realm, Now, Now);

        (await Repo.GetUserAsync(Store.TenantB, Realm, user)).ShouldBeNull();
        (await Repo.GetUserByExternalIdAsync(Store.TenantB, Realm, "ext-" + user)).ShouldBeNull();
        (await Repo.ListUsersAsync(Store.TenantB, Realm)).ShouldBeEmpty();
        (await Repo.GetGroupAsync(Store.TenantB, Realm, group)).ShouldBeNull();
        (await Repo.ListGroupsAsync(Store.TenantB, Realm)).ShouldBeEmpty();
        (await Repo.GetGroupMembersAsync(Store.TenantB, Realm, group)).ShouldBeEmpty();
        (await Repo.GetGroupMembershipsForUserAsync(Store.TenantB, Realm, user)).ShouldBeEmpty();
        (await Repo.ListGroupNestingEdgesAsync(Store.TenantB, Realm)).ShouldBeEmpty();
        (await Repo.IsSubjectLockedAsync(Store.TenantB, Realm, user)).ShouldBeFalse();
        (await Repo.GetLocksAsync(Store.TenantB, Realm)).ShouldBeEmpty();
        (await Repo.CountLocksSinceAsync(Store.TenantB, Realm, ["SCIM_DELETE"], Now.AddHours(-1), 10)).Count.ShouldBe(0);
        (await Repo.GetRealmSyncStateAsync(Store.TenantB, Realm)).ShouldBeNull();
        (await Repo.GetUserAsync(Store.TenantA, "realm-other", user)).ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var key = Id("u");
        var groupKey = Id("g");
        foreach (var tenant in new[] { Store.TenantA, Store.TenantB })
        {
            await Repo.UpsertUserAsync(User(key), tenant, Realm);
            await Repo.UpsertGroupAsync(Group(groupKey), tenant, Realm);
            await Repo.AddGroupMemberAsync(tenant, Realm, groupKey, key);
            await Repo.AddLockAsync(new ScimLock(tenant, Realm, key, "reason", "SCIM_DELETE", Now));
        }

        await Repo.DeactivateUserAsync(Store.TenantB, Realm, key);
        await Repo.RemoveLockAsync(Store.TenantB, Realm, key);
        await Repo.RemoveGroupMemberAsync(Store.TenantB, Realm, groupKey, key);
        (await Repo.DeleteGroupAsync(Store.TenantB, Realm, groupKey)).ShouldBeEmpty();
        await Repo.DeleteUserAsync(Store.TenantB, Realm, key);
        await Repo.IncrementRealmDeprovisionEpochAsync(Store.TenantB, Realm);

        var kept = (await Repo.GetUserAsync(Store.TenantA, Realm, key)).ShouldNotBeNull();
        kept.Active.ShouldBeTrue();
        (await Repo.GetGroupAsync(Store.TenantA, Realm, groupKey)).ShouldNotBeNull();
        (await Repo.GetGroupMembersAsync(Store.TenantA, Realm, groupKey)).ShouldBe([key]);
        (await Repo.IsSubjectLockedAsync(Store.TenantA, Realm, key)).ShouldBeTrue();
        (await EpochAsync(Store.TenantA)).ShouldBe(0);
        (await Repo.GetUserAsync(Store.TenantB, Realm, key)).ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;
        var key = Id("u");

        Should.Throw<ArgumentException>(() => { _ = new TenantId("invalid tenant!"); });
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetUserAsync(default, Realm, key));
        await Should.ThrowAsync<ArgumentException>(() => Repo.UpsertUserAsync(User(key), default, Realm));
        await Should.ThrowAsync<ArgumentException>(() => Repo.DeactivateUserAsync(default, Realm, key));
        await Should.ThrowAsync<ArgumentException>(() => Repo.DeleteUserAsync(default, Realm, key));
        await Should.ThrowAsync<ArgumentException>(() => Repo.ListUsersAsync(default, Realm));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetGroupAsync(default, Realm, key));
        await Should.ThrowAsync<ArgumentException>(() => Repo.UpsertGroupAsync(Group(key), default, Realm));
        await Should.ThrowAsync<ArgumentException>(() => Repo.DeleteGroupAsync(default, Realm, key));
        await Should.ThrowAsync<ArgumentException>(() => Repo.AddGroupMemberAsync(default, Realm, key, key));
        await Should.ThrowAsync<ArgumentException>(() => Repo.AddLockAsync(new ScimLock(default, Realm, key, "reason", "SCIM_DELETE", Now)));
        await Should.ThrowAsync<ArgumentException>(() => Repo.IsSubjectLockedAsync(default, Realm, key));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetLocksAsync(default, Realm));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetRealmSyncStateAsync(default, Realm));
        await Should.ThrowAsync<ArgumentException>(() => Repo.IncrementRealmDeprovisionEpochAsync(default, Realm));

        (await Repo.ListUsersAsync(Store.TenantA, Realm)).ShouldBeEmpty();
        (await Repo.ListGroupsAsync(Store.TenantA, Realm)).ShouldBeEmpty();
        (await Repo.GetLocksAsync(Store.TenantA, Realm)).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;
        var key = Id("u");
        var group = Id("g");
        await Repo.UpsertUserAsync(User(key), Store.TenantA, Realm);
        await Repo.UpsertGroupAsync(Group(group), Store.TenantA, Realm);

        await Repo.UpsertUserAsync(User(key) with { DisplayName = "Renamed" }, Store.TenantA, Realm);
        await Repo.UpsertGroupAsync(Group(group, "Renamed"), Store.TenantA, Realm);
        await Repo.AddGroupMemberAsync(Store.TenantA, Realm, group, key);
        await Repo.AddGroupMemberAsync(Store.TenantA, Realm, group, key);
        await Repo.AddGroupNestingEdgeAsync(Store.TenantA, Realm, group, "child-1");
        await Repo.AddGroupNestingEdgeAsync(Store.TenantA, Realm, group, "child-1");
        await Repo.AddLockAsync(new ScimLock(Store.TenantA, Realm, key, "first", "SCIM_PUT", Now));
        await Repo.AddLockAsync(new ScimLock(Store.TenantA, Realm, key, "second", "SCIM_DELETE", Now.AddMinutes(1)));
        await Repo.UpsertRealmSyncStateAsync(Store.TenantA, Realm, Now, Now);
        await Repo.UpsertRealmSyncStateAsync(Store.TenantA, Realm, Now.AddMinutes(5), null);

        (await Repo.ListUsersAsync(Store.TenantA, Realm)).ShouldHaveSingleItem().DisplayName.ShouldBe("Renamed");
        (await Repo.ListGroupsAsync(Store.TenantA, Realm)).ShouldHaveSingleItem().DisplayName.ShouldBe("Renamed");
        (await Repo.GetGroupMembersAsync(Store.TenantA, Realm, group)).ShouldBe([key]);
        (await Repo.ListGroupNestingEdgesAsync(Store.TenantA, Realm)).ShouldHaveSingleItem().ChildGroupId.ShouldBe("child-1");
        var locked = (await Repo.GetLocksAsync(Store.TenantA, Realm)).ShouldHaveSingleItem();
        locked.Reason.ShouldBe("second");
        locked.Source.ShouldBe("SCIM_DELETE");
        locked.LockedAt.ShouldBe(Now.AddMinutes(1));
        var state = (await Repo.GetRealmSyncStateAsync(Store.TenantA, Realm)).ShouldNotBeNull();
        state.LastContactAt.ShouldBe(Now.AddMinutes(5));
        state.LastWriteAt.ShouldBe(Now);
        state.DeprovisionEpoch.ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        var first = Id("u");
        var second = Id("u");
        await Repo.CreateUserAsync(Store.TenantA, Realm, User(first, "shared-ext"));
        var firstGroup = Id("g");
        await Repo.CreateGroupAsync(Store.TenantA, Realm, Group(firstGroup, externalId: "shared-group-ext"));

        var user = await Should.ThrowAsync<ScimUniquenessConflictException>(
            () => Repo.CreateUserAsync(Store.TenantA, Realm, User(second, "shared-ext")));
        var group = await Should.ThrowAsync<ScimUniquenessConflictException>(
            () => Repo.CreateGroupAsync(Store.TenantA, Realm, Group(Id("g"), externalId: "shared-group-ext")));

        user.AttributeName.ShouldBe("externalId");
        group.AttributeName.ShouldBe("externalId");
        (await Repo.GetUserAsync(Store.TenantA, Realm, second)).ShouldBeNull();
        (await Repo.ListUsersAsync(Store.TenantA, Realm)).ShouldHaveSingleItem().Id.ShouldBe(first);
        (await Repo.ListGroupsAsync(Store.TenantA, Realm)).ShouldHaveSingleItem().Id.ShouldBe(firstGroup);

        // The key is scoped by tenant and realm, and re-creating the same user key updates in place.
        await Repo.CreateUserAsync(Store.TenantB, Realm, User(second, "shared-ext"));
        await Repo.CreateUserAsync(Store.TenantA, "realm-2", User(second, "shared-ext"));
        await Repo.CreateUserAsync(Store.TenantA, Realm, User(first, "shared-ext"));
        (await Repo.ListUsersAsync(Store.TenantA, Realm)).ShouldHaveSingleItem().Id.ShouldBe(first);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        foreach (var n in new[] { 3, 1, 4, 2, 0 })
        {
            await Repo.UpsertUserAsync(User($"p-{n:D2}"), Store.TenantA, Realm);
            await Repo.UpsertGroupAsync(Group($"p-{n:D2}"), Store.TenantA, Realm);
        }

        (await Repo.ListUsersAsync(Store.TenantA, Realm, 1, 2)).Select(u => u.Id).ShouldBe(["p-00", "p-01"]);
        (await Repo.ListUsersAsync(Store.TenantA, Realm, 3, 2)).Select(u => u.Id).ShouldBe(["p-02", "p-03"]);
        (await Repo.ListUsersAsync(Store.TenantA, Realm, 5, 2)).Select(u => u.Id).ShouldBe(["p-04"]);
        (await Repo.ListUsersAsync(Store.TenantA, Realm, 6, 2)).ShouldBeEmpty();
        (await Repo.ListUsersAsync(Store.TenantA, Realm, 0, 2)).Select(u => u.Id).ShouldBe(["p-00", "p-01"]);
        (await Repo.ListGroupsAsync(Store.TenantA, Realm, 2, 3)).Select(g => g.Id).ShouldBe(["p-01", "p-02", "p-03"]);
        (await Repo.ListUsersAsync(Store.TenantA, Realm, 1, 2)).Select(u => u.Id).ShouldBe(["p-00", "p-01"]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        var realm = Id("realm");
        var now = Now;
        for (var i = 0; i < 5; i++)
        {
            await Repo.UpsertUserAsync(User($"c-{i}"), Store.TenantA, realm);
            await Repo.AddLockAsync(new ScimLock(Store.TenantA, realm, $"c-{i}", "r", "SCIM_DELETE", now.AddMinutes(-i)));
        }

        // The store itself applies no page cap (callers clamp): an oversized page returns everything, never an error.
        (await Repo.ListUsersAsync(Store.TenantA, realm, 1, 100000)).Count.ShouldBe(5);

        // The lock window scan is bounded by its cap, and a non-positive cap or an empty source list reads nothing.
        (await Repo.CountLocksSinceAsync(Store.TenantA, realm, ["SCIM_DELETE"], now.AddHours(-1), 3)).Count.ShouldBe(3);
        (await Repo.CountLocksSinceAsync(Store.TenantA, realm, ["SCIM_DELETE"], now.AddHours(-1), 100000)).Count.ShouldBe(5);
        (await Repo.CountLocksSinceAsync(Store.TenantA, realm, ["SCIM_DELETE"], now.AddHours(-1), 0)).Count.ShouldBe(0);
        (await Repo.CountLocksSinceAsync(Store.TenantA, realm, [], now.AddHours(-1), 10)).Count.ShouldBe(0);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        var (user, group) = await UserInGroupAsync(Store.TenantA);
        var (otherUser, otherGroup) = await UserInGroupAsync(Store.TenantA);
        await Repo.AddLockAsync(new ScimLock(Store.TenantA, Realm, user, "reason", "SCIM_DELETE", Now));

        await Repo.DeactivateUserAsync(Store.TenantA, Realm, user);
        (await Repo.GetGroupMembershipsForUserAsync(Store.TenantA, Realm, user)).ShouldBeEmpty();
        (await Repo.GetGroupMembersAsync(Store.TenantA, Realm, group)).ShouldBeEmpty();
        (await Repo.GetUserAsync(Store.TenantA, Realm, user))!.Active.ShouldBeFalse();

        await Repo.DeleteUserAsync(Store.TenantA, Realm, user);
        await Repo.RemoveLockAsync(Store.TenantA, Realm, user);
        await Repo.DeleteGroupAsync(Store.TenantA, Realm, otherGroup);

        (await Repo.GetUserAsync(Store.TenantA, Realm, user)).ShouldBeNull();
        (await Repo.IsSubjectLockedAsync(Store.TenantA, Realm, user)).ShouldBeFalse();
        (await Repo.GetLocksAsync(Store.TenantA, Realm)).ShouldBeEmpty();
        (await Repo.GetGroupAsync(Store.TenantA, Realm, otherGroup)).ShouldBeNull();
        (await Repo.ListGroupsAsync(Store.TenantA, Realm)).ShouldHaveSingleItem().Id.ShouldBe(group);
        (await Repo.GetGroupMembershipsForUserAsync(Store.TenantA, Realm, otherUser)).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public async Task User_RoundTrip_AllColumns_IncludingInactiveAndNullTimestamps()
    {
        if (!Store.IsAvailable) return;
        var key = Id("u");
        var created = Now.AddDays(-2);
        var deactivated = Now.AddHours(-1);

        await Repo.UpsertUserAsync(
            new ScimUser(key, "ext-" + key, "user@example.com", "Display", "user@example.com", false, deactivated, created, created), Store.TenantA, Realm);

        var read = (await Repo.GetUserAsync(Store.TenantA, Realm, key)).ShouldNotBeNull();
        read.Active.ShouldBeFalse();
        read.DeactivatedAt.ShouldBe(deactivated);
        read.CreatedAt.ShouldBe(created);

        var key2 = Id("u");
        await Repo.UpsertUserAsync(new ScimUser(key2, "ext-" + key2, "second@example.com", null, null, true, null, created, created), Store.TenantA, Realm);
        var active = (await Repo.GetUserAsync(Store.TenantA, Realm, key2)).ShouldNotBeNull();
        active.Active.ShouldBeTrue();
        active.DeactivatedAt.ShouldBeNull();
        active.Email.ShouldBeNull();
        (await Repo.GetUserByExternalIdAsync(Store.TenantA, Realm, "ext-" + key2))!.Id.ShouldBe(key2);
    }

    [RequiresContainerRuntimeFact]
    public async Task User_Deactivate_SetsInactiveAndTimestamp()
    {
        if (!Store.IsAvailable) return;
        var key = Id("u");
        await Repo.UpsertUserAsync(User(key), Store.TenantA, Realm);

        await Repo.DeactivateUserAsync(Store.TenantA, Realm, key);

        var read = (await Repo.GetUserAsync(Store.TenantA, Realm, key)).ShouldNotBeNull();
        read.Active.ShouldBeFalse();
        read.DeactivatedAt.ShouldNotBeNull();
    }

    [RequiresContainerRuntimeFact]
    public async Task Lock_RoundTrip_LockedAtPreserved()
    {
        if (!Store.IsAvailable) return;
        var key = Id("u");
        var lockedAt = Now.AddMinutes(-5);

        await Repo.AddLockAsync(new ScimLock(Store.TenantA, Realm, key, "reason", "SCIM_DELETE", lockedAt));

        (await Repo.IsSubjectLockedAsync(Store.TenantA, Realm, key)).ShouldBeTrue();
        (await Repo.GetLocksAsync(Store.TenantA, Realm)).ShouldContain(l => l.UserKey == key && l.LockedAt == lockedAt);
        await Repo.RemoveLockAsync(Store.TenantA, Realm, key);
        (await Repo.IsSubjectLockedAsync(Store.TenantA, Realm, key)).ShouldBeFalse();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task WAVE3_SCIM_T10_CountLocksSince_ScopedDistinctWindowedAndBounded()
    {
        if (!Store.IsAvailable) return;
        var realm = Id("realm");
        var other = Id("realm");
        var otherTenant = Store.TenantB;
        var now = Now;
        string[] scim = ["SCIM_PUT", "SCIM_PATCH", "SCIM_DELETE"];

        await Repo.AddLockAsync(new ScimLock(Store.TenantA, realm, "a", "r", "SCIM_DELETE", now.AddMinutes(-30)));
        await Repo.AddLockAsync(new ScimLock(Store.TenantA, realm, "b", "r", "SCIM_PUT", now.AddMinutes(-20)));
        await Repo.AddLockAsync(new ScimLock(Store.TenantA, realm, "c", "r", "SCIM_PATCH", now.AddMinutes(-10)));
        await Repo.AddLockAsync(new ScimLock(Store.TenantA, realm, "old", "r", "SCIM_DELETE", now.AddMinutes(-90)));
        await Repo.AddLockAsync(new ScimLock(Store.TenantA, realm, "admin", "r", "admin-sid", now.AddMinutes(-5)));
        await Repo.AddLockAsync(new ScimLock(Store.TenantA, other, "x", "r", "SCIM_DELETE", now.AddMinutes(-5)));
        await Repo.AddLockAsync(new ScimLock(otherTenant, realm, "y", "r", "SCIM_DELETE", now.AddMinutes(-5)));
        // a repeat on an already-locked user is one row, still counted once
        await Repo.AddLockAsync(new ScimLock(Store.TenantA, realm, "a", "r", "SCIM_DELETE", now.AddMinutes(-29)));

        var window = await Repo.CountLocksSinceAsync(Store.TenantA, realm, scim, now.AddMinutes(-60), 100);
        window.Count.ShouldBe(3);
        window.Oldest.ShouldBe(now.AddMinutes(-29));

        (await Repo.CountLocksSinceAsync(Store.TenantA, realm, scim, now.AddMinutes(-60), 2)).Count.ShouldBe(2);
        var empty = await Repo.CountLocksSinceAsync(Store.TenantA, Id("none"), scim, now.AddMinutes(-60), 5);
        (empty.Count, empty.Oldest).ShouldBe((0, null));
    }

    [RequiresContainerRuntimeFact]
    public async Task DeleteGroup_RemovesMemberships_AndReturnsFormerMembers()
    {
        if (!Store.IsAvailable) return;
        var (user, group) = await UserInGroupAsync(Store.TenantA);
        (await Repo.GetGroupMembershipsForUserAsync(Store.TenantA, Realm, user)).ShouldContain(group);

        var former = await Repo.DeleteGroupAsync(Store.TenantA, Realm, group);

        former.ShouldContain(user);
        (await Repo.GetGroupMembershipsForUserAsync(Store.TenantA, Realm, user)).ShouldNotContain(group);
        (await Repo.GetGroupMembersAsync(Store.TenantA, Realm, group)).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public async Task DeleteGroup_RecreatedSameId_HasNoStaleMembers()
    {
        if (!Store.IsAvailable) return;
        var (user, group) = await UserInGroupAsync(Store.TenantA);
        await Repo.DeleteGroupAsync(Store.TenantA, Realm, group);

        await Repo.UpsertGroupAsync(Group(group, "Recreated"), Store.TenantA, Realm);

        (await Repo.GetGroupMembershipsForUserAsync(Store.TenantA, Realm, user)).ShouldNotContain(group);
    }

    [RequiresContainerRuntimeFact]
    public async Task DeleteGroup_RemovesNestingEdgesOfTheGroup()
    {
        if (!Store.IsAvailable) return;
        var parent = Id("g");
        var middle = Id("g");
        var child = Id("g");
        await Repo.AddGroupNestingEdgeAsync(Store.TenantA, Realm, parent, middle);
        await Repo.AddGroupNestingEdgeAsync(Store.TenantA, Realm, middle, child);
        (await Repo.GetChildGroupIdsAsync(Store.TenantA, Realm, parent)).ShouldBe([middle]);

        await Repo.DeleteGroupAsync(Store.TenantA, Realm, middle);

        (await Repo.ListGroupNestingEdgesAsync(Store.TenantA, Realm)).ShouldBeEmpty();
        (await Repo.GetChildGroupIdsAsync(Store.TenantA, Realm, parent)).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public async Task DeleteUser_RemovesMemberships_KeepsLock()
    {
        if (!Store.IsAvailable) return;
        var (user, group) = await UserInGroupAsync(Store.TenantA);
        await Repo.AddLockAsync(new ScimLock(Store.TenantA, Realm, user, "deprovisioned", "SCIM_DELETE", Now));

        await Repo.DeleteUserAsync(Store.TenantA, Realm, user);

        (await Repo.GetUserAsync(Store.TenantA, Realm, user)).ShouldBeNull();
        (await Repo.GetGroupMembersAsync(Store.TenantA, Realm, group)).ShouldNotContain(user);
        (await Repo.IsSubjectLockedAsync(Store.TenantA, Realm, user)).ShouldBeTrue();
    }

    [RequiresContainerRuntimeFact]
    public async Task Memberships_OrphanRow_NotReturned()
    {
        if (!Store.IsAvailable) return;
        var user = Id("u");
        var missingGroup = Id("g");

        // SCIM may add members before the group exists; such an orphan never resolves to a group.
        await Repo.AddGroupMemberAsync(Store.TenantA, Realm, missingGroup, user);

        (await Repo.GetGroupMembershipsForUserAsync(Store.TenantA, Realm, user)).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task DeprovisioningSteps_BumpTheRealmEpochOnce_AndPlainWritesDoNot()
    {
        if (!Store.IsAvailable) return;
        var (user, group) = await UserInGroupAsync(Store.TenantA);
        (await EpochAsync(Store.TenantA)).ShouldBe(0);

        (await Repo.IncrementRealmDeprovisionEpochAsync(Store.TenantA, Realm)).ShouldBe(1);
        await Repo.UpsertUserAsync(User(user) with { DisplayName = "Renamed" }, Store.TenantA, Realm);
        await Repo.DeleteGroupAsync(Store.TenantA, Realm, group);
        (await EpochAsync(Store.TenantA)).ShouldBe(1);

        await Repo.DeactivateUserAsync(Store.TenantA, Realm, user);
        (await EpochAsync(Store.TenantA)).ShouldBe(2);
        await Repo.UpdateUserAsync(Store.TenantA, Realm, User(user, active: false));
        (await EpochAsync(Store.TenantA)).ShouldBe(3);
        await Repo.DeleteUserAsync(Store.TenantA, Realm, user);
        (await EpochAsync(Store.TenantA)).ShouldBe(4);
        (await EpochAsync(Store.TenantB)).ShouldBe(0);
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlIdentityStoreContractTests(GovernancePostgresFixture fixture) : IdentityStoreContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "identity_store_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerIdentityStoreContractTests(GovernanceSqlServerFixture fixture) : IdentityStoreContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "identity_store_contract");
}
