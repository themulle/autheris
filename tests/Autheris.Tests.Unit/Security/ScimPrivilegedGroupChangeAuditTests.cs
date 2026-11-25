namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Governance.Notifications;
using Autheris.Application.Identity.Caching;
using Autheris.Application.Identity.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Cache;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ScimPrivilegedGroupChangeAuditTests
{
    private const string RealmId = "workforce-entra";
    private const string OwnerGroupSid = "S-1-5-21-100-200-300-9101";
    private const string EmployeeGroupSid = "S-1-5-21-100-200-300-9102";
    private const string MappedGroupSid = "S-1-5-21-100-200-300-9103";
    private const string RegularGroupSid = "S-1-5-21-100-200-300-9104";
    private static readonly TenantId Tenant = new("tenant-priv");

    [Fact]
    public async Task Owner_group_change_audited_and_notified()
    {
        var t0 = new DateTimeOffset(2026, 11, 22, 14, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(t0);
        var store = new ScimDeprovisioningTests.SharedDeprovisionStore();
        store.SeedUser("u-1", "oid-u-1", active: true);
        store.SeedGroup("g-owner", OwnerGroupSid);

        var domains = new StubGovernanceDomainRepository([
            new GovernanceDomain(
                Guid.NewGuid(),
                Tenant,
                "finance-domain",
                "Finance",
                "owner@corp.example",
                new Sid(OwnerGroupSid),
                ProtectionClass.Confidential,
                CatalogVisibilityMode.OnRequest,
                IsSystemUnassigned: false,
                Version: 1,
                t0,
                t0)
        ]);

        var orderLog = new List<string>();
        var auditWriter = new OrderingAuditWriter(orderLog);
        var dispatcher = new RecordingNotificationDispatcher(orderLog);
        using var changeNotifier = new RedisGovernanceChangeChannel(timeProvider: time);
        var observedChanges = new List<GovernanceChangeEvent>();
        using var sub = changeNotifier.Subscribe(observedChanges.Add);

        var auditor = new PrivilegedGroupChangeAuditor(
            domainRepository: domains,
            auditWriter: auditWriter,
            notificationDispatcher: dispatcher);
        var roleCache = new RoleResolverCache(timeProvider: time, changeNotifier: changeNotifier);
        var scim = new ScimService(
            new OperationTrackingStore(store, orderLog),
            new IdentityDeprovisioningService(store, Autheris.Tests.Unit.TestSupport.NoOpGrantRevocation.Instance, roleResolverCache: roleCache, governanceChangeNotifier: changeNotifier, timeProvider: time),
            roleCache: roleCache,
            timeProvider: time,
            auditWriter: auditWriter,
            governanceChangeNotifier: changeNotifier,
            privilegedGroupAuditor: auditor);

        var patchAdd = new ScimPatchRequest(
            [ScimSchemaUris.PatchOp],
            [new ScimPatchOperation("add", "members", new[] { new ScimGroupMember("u-1") })]);
        await scim.PatchGroupAsync(Tenant, RealmId, "g-owner", patchAdd);

        var privAudit = auditWriter.Entries.Single(e => e.EventType == AuditEventTypes.PrivilegedGroupMembershipChanged);
        privAudit.Decision.ShouldBe("ALLOW");
        privAudit.TenantId.ShouldBe(Tenant);
        privAudit.TargetTable.ShouldBe("IDENTITY_GROUP_MEMBERS");
        using (var doc = JsonDocument.Parse(privAudit.DetailsJson!))
        {
            doc.RootElement.GetProperty("group_id").GetString().ShouldBe("g-owner");
            doc.RootElement.GetProperty("categories").EnumerateArray().Select(x => x.GetString()).ShouldContain("tenant_owner_group");
        }

        dispatcher.Messages.Count.ShouldBe(1);
        dispatcher.Messages[0].EventType.ShouldBe(AuditEventTypes.PrivilegedGroupMembershipChanged);
        dispatcher.Messages[0].TenantId.ShouldBe(Tenant.Value);
        dispatcher.Messages[0].RealmId.ShouldBe(RealmId);
        dispatcher.Messages[0].SubjectOrGroupId.ShouldBe("g-owner");

        observedChanges.ShouldContain(c => c.TenantId == Tenant.Value && c.GroupId == "g-owner");

        // Fail-closed ordering: privileged audit occurs BEFORE DB write, dispatch occurs AFTER DB write.
        var auditIndex = orderLog.IndexOf($"AUDIT:{AuditEventTypes.PrivilegedGroupMembershipChanged}");
        var dbWriteIndex = orderLog.IndexOf("DB:ADD_MEMBER");
        var dispatchIndex = orderLog.IndexOf($"DISPATCH:{AuditEventTypes.PrivilegedGroupMembershipChanged}");
        auditIndex.ShouldBeGreaterThanOrEqualTo(0);
        dbWriteIndex.ShouldBeGreaterThan(auditIndex);
        dispatchIndex.ShouldBeGreaterThan(dbWriteIndex);
    }

    [Fact]
    public async Task Employee_and_role_mapped_group_changes_audited_and_notified()
    {
        var t0 = new DateTimeOffset(2026, 11, 22, 14, 15, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(t0);
        var store = new ScimDeprovisioningTests.SharedDeprovisionStore();
        store.SeedUser("u-2", "oid-u-2", active: true);
        store.SeedGroup("g-emp", EmployeeGroupSid);
        store.SeedGroup("g-mapped", MappedGroupSid);

        var rightRepo = new StubFunctionalRightRepository([
            new FunctionalRightMapping(
                Tenant.Value,
                RealmId,
                MappedGroupSid,
                FunctionalRight.GovernanceAdmin,
                ApprovedBy: "approver-alice",
                ApprovedAt: t0,
                RecertClass: "Privileged",
                CreatedBy: "creator-bob",
                CreatedAt: t0)
        ]);
        var employeeLookup = new StaticEmployeeGroupLookup([EmployeeGroupSid]);
        var auditWriter = new InMemoryAuditWriter();
        var dispatcher = new RecordingNotificationDispatcher();
        var auditor = new PrivilegedGroupChangeAuditor(
            functionalRightRepository: rightRepo,
            employeeGroupLookup: employeeLookup,
            auditWriter: auditWriter,
            notificationDispatcher: dispatcher);

        var scim = new ScimService(
            store,
            new IdentityDeprovisioningService(store, Autheris.Tests.Unit.TestSupport.NoOpGrantRevocation.Instance, timeProvider: time),
            timeProvider: time,
            auditWriter: auditWriter,
            privilegedGroupAuditor: auditor);

        await scim.PatchGroupAsync(Tenant, RealmId, "g-emp",
            new ScimPatchRequest([ScimSchemaUris.PatchOp], [new ScimPatchOperation("add", "members", new[] { new ScimGroupMember("u-2") })]));
        await scim.PatchGroupAsync(Tenant, RealmId, "g-mapped",
            new ScimPatchRequest([ScimSchemaUris.PatchOp], [new ScimPatchOperation("add", "members", new[] { new ScimGroupMember("u-2") })]));

        var privEntries = auditWriter.Entries
            .Where(e => e.EventType == AuditEventTypes.PrivilegedGroupMembershipChanged)
            .ToList();
        privEntries.Count.ShouldBe(2);
        privEntries[0].DetailsJson!.ShouldContain("employee_group");
        privEntries[1].DetailsJson!.ShouldContain("role_mapped_group");
        dispatcher.Messages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Audit_failure_aborts_privileged_group_change_fail_closed()
    {
        var t0 = new DateTimeOffset(2026, 11, 22, 14, 30, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(t0);
        var store = new ScimDeprovisioningTests.SharedDeprovisionStore();
        store.SeedUser("u-3", "oid-u-3", active: true);
        store.SeedGroup("g-owner", OwnerGroupSid);

        var domains = new StubGovernanceDomainRepository([
            new GovernanceDomain(
                Guid.NewGuid(), Tenant, "owner-domain", null, null,
                new Sid(OwnerGroupSid), ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest,
                false, 1, t0, t0)
        ]);
        var failingAudit = new InMemoryAuditWriter(failure: new AuditUnavailableException("Audit store unreachable"));
        var dispatcher = new RecordingNotificationDispatcher();
        using var notifier = new RedisGovernanceChangeChannel(timeProvider: time);
        var notified = new List<GovernanceChangeEvent>();
        using var sub = notifier.Subscribe(notified.Add);

        var auditor = new PrivilegedGroupChangeAuditor(
            domainRepository: domains,
            auditWriter: failingAudit,
            notificationDispatcher: dispatcher);
        var scim = new ScimService(
            store,
            new IdentityDeprovisioningService(store, Autheris.Tests.Unit.TestSupport.NoOpGrantRevocation.Instance, timeProvider: time),
            timeProvider: time,
            auditWriter: failingAudit,
            governanceChangeNotifier: notifier,
            privilegedGroupAuditor: auditor);

        await Should.ThrowAsync<AuditUnavailableException>(() =>
            scim.PatchGroupAsync(Tenant, RealmId, "g-owner",
                new ScimPatchRequest([ScimSchemaUris.PatchOp], [new ScimPatchOperation("add", "members", new[] { new ScimGroupMember("u-3") })])));

        store.Memberships.ShouldNotContain(("g-owner", "u-3"));
        dispatcher.Messages.ShouldBeEmpty();
        notified.ShouldBeEmpty();
    }

    [Fact]
    public async Task Non_privileged_group_change_invalidates_caches_without_privileged_audit()
    {
        var t0 = new DateTimeOffset(2026, 11, 22, 14, 45, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(t0);
        var store = new ScimDeprovisioningTests.SharedDeprovisionStore();
        store.SeedUser("u-4", "oid-u-4", active: true);
        store.SeedGroup("g-regular", RegularGroupSid);

        var auditWriter = new InMemoryAuditWriter();
        var dispatcher = new RecordingNotificationDispatcher();
        using var notifier = new RedisGovernanceChangeChannel(timeProvider: time);
        var notified = new List<GovernanceChangeEvent>();
        using var sub = notifier.Subscribe(notified.Add);

        var auditor = new PrivilegedGroupChangeAuditor(
            domainRepository: new StubGovernanceDomainRepository([]),
            functionalRightRepository: new StubFunctionalRightRepository([]),
            employeeGroupLookup: EmptyEmployeeGroupLookup.Instance,
            auditWriter: auditWriter,
            notificationDispatcher: dispatcher);
        var scim = new ScimService(
            store,
            new IdentityDeprovisioningService(store, Autheris.Tests.Unit.TestSupport.NoOpGrantRevocation.Instance, timeProvider: time),
            timeProvider: time,
            auditWriter: auditWriter,
            governanceChangeNotifier: notifier,
            privilegedGroupAuditor: auditor);

        await scim.PatchGroupAsync(Tenant, RealmId, "g-regular",
            new ScimPatchRequest([ScimSchemaUris.PatchOp], [new ScimPatchOperation("add", "members", new[] { new ScimGroupMember("u-4") })]));

        auditWriter.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ScimGroupMembershipChanged);
        auditWriter.Entries.ShouldNotContain(e => e.EventType == AuditEventTypes.PrivilegedGroupMembershipChanged);
        dispatcher.Messages.ShouldBeEmpty();
        notified.ShouldContain(c => c.GroupId == "g-regular");
    }

    private sealed class OrderingAuditWriter(List<string>? orderLog = null) : IAuditWriter
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            orderLog?.Add($"AUDIT:{entry.EventType}");
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => RecordRequiredAsync(entry, ct);
    }

    private sealed class RecordingNotificationDispatcher(List<string>? orderLog = null) : IGovernanceNotificationDispatcher
    {
        public List<GovernanceNotificationMessage> Messages { get; } = [];

        public Task DispatchAsync(GovernanceNotificationMessage message, CancellationToken ct = default)
        {
            orderLog?.Add($"DISPATCH:{message.EventType}");
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class StaticEmployeeGroupLookup(IReadOnlyCollection<string> sids) : IEmployeeGroupLookup
    {
        public IReadOnlyCollection<string> GetEmployeeGroupSids(TenantId tenantId) => sids;
    }

    private sealed class StubGovernanceDomainRepository(IReadOnlyList<GovernanceDomain> domains) : IGovernanceDomainRepository
    {
        public Task<IReadOnlyList<GovernanceDomain>> ListAsync(TenantId tenant, int limit = 100, int offset = 0, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GovernanceDomain>>(
                offset == 0 ? domains.Where(d => d.Tenant == tenant).Skip(offset).Take(limit).ToList() : []);

        public Task<GovernanceDomain?> GetAsync(TenantId tenant, Guid domainId, CancellationToken ct = default) =>
            Task.FromResult<GovernanceDomain?>(null);

        public Task<GovernanceDomainUpsertResult> UpsertAsync(GovernanceDomain domain, int expectedVersion, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(TenantId tenant, Guid domainId, int expectedVersion, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GovernanceDomain> EnsureUnassignedAsync(TenantId tenant, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task AssignTablesAsync(TenantId tenant, Guid domainId, IReadOnlyList<Guid> tableIds, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<(ObjectProtection? Table, IReadOnlyDictionary<string, ObjectProtection> Columns)> GetProtectionAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SetProtectionAsync(TenantId tenant, Guid tableId, string column, ObjectProtection protection, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SetTableProtectionAsync(TenantId tenant, Guid tableId, ObjectProtection protection, IReadOnlyList<string>? clearColumns, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ContainerDefaults> GetContainerForTableAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubFunctionalRightRepository(IReadOnlyList<FunctionalRightMapping> mappings) : IFunctionalRightRepository
    {
        public Task<IReadOnlyList<FunctionalRightMapping>> GetMappingsAsync(TenantId tenantId, string realmId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FunctionalRightMapping>>(mappings.Where(m => m.TenantId == tenantId && m.RealmId == realmId).ToList());

        public Task<IReadOnlyList<FunctionalRightMapping>> GetApprovedMappingsForGroupsAndRolesAsync(
            TenantId tenantId, string realmId, IReadOnlyCollection<string> groupsAndRoles, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FunctionalRightMapping>>(
                mappings.Where(m => m.TenantId == tenantId && m.RealmId == realmId && m.ApprovedBy is not null && groupsAndRoles.Contains(m.GroupOrRole, StringComparer.OrdinalIgnoreCase)).ToList());

        public Task<FunctionalRightMapping?> GetMappingAsync(TenantId tenantId, string realmId, string groupOrRole, FunctionalRight functionalRight, CancellationToken ct = default) =>
            Task.FromResult<FunctionalRightMapping?>(null);

        public Task CreateMappingAsync(FunctionalRightMapping mapping, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteMappingAsync(TenantId tenantId, string realmId, string groupOrRole, FunctionalRight functionalRight, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class OperationTrackingStore(ScimDeprovisioningTests.SharedDeprovisionStore inner, List<string> log) : IIdentityStoreRepository
    {
        public Task<ScimUser?> GetUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => inner.GetUserAsync(tenantId, realmId, userKey, ct);
        public Task<ScimUser?> GetUserByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) => inner.GetUserByExternalIdAsync(tenantId, realmId, externalId, ct);
        public Task<ScimUser?> GetUserByUserNameAsync(TenantId tenantId, string realmId, string userName, CancellationToken ct = default) => inner.GetUserByUserNameAsync(tenantId, realmId, userName, ct);
        public Task<ScimUser?> GetUserByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default) => inner.GetUserByObjectSidAsync(tenantId, realmId, objectSid, ct);
        public Task<ScimUser?> GetUserByImmutableIdAsync(TenantId tenantId, string realmId, string onPremisesImmutableId, CancellationToken ct = default) => inner.GetUserByImmutableIdAsync(tenantId, realmId, onPremisesImmutableId, ct);
        public Task<ScimUser?> GetUserByEntraObjectIdAsync(TenantId tenantId, string realmId, string entraObjectId, CancellationToken ct = default) => inner.GetUserByEntraObjectIdAsync(tenantId, realmId, entraObjectId, ct);
        public Task<ScimUser?> GetUserByUpnAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default) => inner.GetUserByUpnAsync(tenantId, realmId, userPrincipalName, ct);
        public Task<IReadOnlyList<ScimUser>> GetUsersByEntraObjectIdOrExternalIdAsync(TenantId tenantId, string realmId, string identifier, CancellationToken ct = default) => inner.GetUsersByEntraObjectIdOrExternalIdAsync(tenantId, realmId, identifier, ct);
        public Task<IReadOnlyList<ScimUser>> GetUsersByUpnOrUserNameAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default) => inner.GetUsersByUpnOrUserNameAsync(tenantId, realmId, userPrincipalName, ct);
        public Task UpsertUserAsync(ScimUser user, TenantId tenantId, string realmId, CancellationToken ct = default) => inner.UpsertUserAsync(user, tenantId, realmId, ct);
        public Task CreateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default) => inner.CreateUserAsync(tenantId, realmId, user, ct);
        public Task UpdateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default) => inner.UpdateUserAsync(tenantId, realmId, user, ct);
        public Task DeactivateUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => inner.DeactivateUserAsync(tenantId, realmId, userKey, ct);
        public Task DeleteUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => inner.DeleteUserAsync(tenantId, realmId, userKey, ct);
        public Task<IReadOnlyList<ScimUser>> ListUsersAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default) => inner.ListUsersAsync(tenantId, realmId, startIndex, count, filter, ct);
        public Task<ScimGroup?> GetGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => inner.GetGroupAsync(tenantId, realmId, groupId, ct);
        public Task<ScimGroup?> GetGroupByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) => inner.GetGroupByExternalIdAsync(tenantId, realmId, externalId, ct);
        public Task<ScimGroup?> GetGroupByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default) => inner.GetGroupByObjectSidAsync(tenantId, realmId, objectSid, ct);
        public Task UpsertGroupAsync(ScimGroup group, TenantId tenantId, string realmId, CancellationToken ct = default) => inner.UpsertGroupAsync(group, tenantId, realmId, ct);
        public Task CreateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default) => inner.CreateGroupAsync(tenantId, realmId, group, ct);
        public Task UpdateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default) => inner.UpdateGroupAsync(tenantId, realmId, group, ct);
        public Task<IReadOnlyList<string>> DeleteGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => inner.DeleteGroupAsync(tenantId, realmId, groupId, ct);
        public Task<IReadOnlyList<ScimGroup>> ListGroupsAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default) => inner.ListGroupsAsync(tenantId, realmId, startIndex, count, filter, ct);
        public Task AddGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default)
        {
            log.Add("DB:ADD_MEMBER");
            return inner.AddGroupMemberAsync(tenantId, realmId, groupId, userKey, ct);
        }
        public Task RemoveGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default)
        {
            log.Add("DB:REMOVE_MEMBER");
            return inner.RemoveGroupMemberAsync(tenantId, realmId, groupId, userKey, ct);
        }
        public Task<IReadOnlyList<string>> GetGroupMembershipsForUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => inner.GetGroupMembershipsForUserAsync(tenantId, realmId, userKey, ct);
        public Task<IReadOnlyList<string>> GetGroupMembersAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => inner.GetGroupMembersAsync(tenantId, realmId, groupId, ct);
        public Task AddGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default) => inner.AddGroupNestingEdgeAsync(tenantId, realmId, parentGroupId, childGroupId, ct);
        public Task RemoveGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default) => inner.RemoveGroupNestingEdgeAsync(tenantId, realmId, parentGroupId, childGroupId, ct);
        public Task<IReadOnlyList<string>> GetChildGroupIdsAsync(TenantId tenantId, string realmId, string parentGroupId, CancellationToken ct = default) => inner.GetChildGroupIdsAsync(tenantId, realmId, parentGroupId, ct);
        public Task<IReadOnlyList<ScimGroupNestingEdge>> ListGroupNestingEdgesAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => inner.ListGroupNestingEdgesAsync(tenantId, realmId, ct);
        public Task<EffectiveScimGroupsResult> GetEffectiveGroupSidsForUserAsync(TenantId tenantId, string realmId, string userKey, int maxDepth = 32, CancellationToken ct = default) => inner.GetEffectiveGroupSidsForUserAsync(tenantId, realmId, userKey, maxDepth, ct);
        public Task AddLockAsync(ScimLock lockEntry, CancellationToken ct = default) => inner.AddLockAsync(lockEntry, ct);
        public Task<bool> IsSubjectLockedAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => inner.IsSubjectLockedAsync(tenantId, realmId, userKey, ct);
        public Task<IReadOnlyList<ScimLock>> GetLocksAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => inner.GetLocksAsync(tenantId, realmId, ct);
        public Task<ScimLockWindow> CountLocksSinceAsync(TenantId tenantId, string realmId, IReadOnlyList<string> sources, DateTimeOffset sinceUtc, int cap, CancellationToken ct = default) => inner.CountLocksSinceAsync(tenantId, realmId, sources, sinceUtc, cap, ct);
        public Task RemoveLockAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => inner.RemoveLockAsync(tenantId, realmId, userKey, ct);
        public Task<ScimRealmSyncState?> GetRealmSyncStateAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => inner.GetRealmSyncStateAsync(tenantId, realmId, ct);
        public Task UpsertRealmSyncStateAsync(TenantId tenantId, string realmId, DateTimeOffset lastContactAt, DateTimeOffset? lastWriteAt, CancellationToken ct = default) => inner.UpsertRealmSyncStateAsync(tenantId, realmId, lastContactAt, lastWriteAt, ct);
        public Task<long> IncrementRealmDeprovisionEpochAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => inner.IncrementRealmDeprovisionEpochAsync(tenantId, realmId, ct);
    }
}
