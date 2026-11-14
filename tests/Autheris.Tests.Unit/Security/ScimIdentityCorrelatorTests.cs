namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Identity;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;
using AuthenticationOptions = Autheris.Domain.Options.AuthenticationOptions;

[Trait("Category", "Security")]
public sealed class ScimIdentityCorrelatorTests
{
    private const string SidA = "S-1-5-21-100-200-300-1101";
    private const string SidB = "S-1-5-21-100-200-300-1102";
    private const string ImmutableA = "imm-abc-123";
    private const string OidA = "11111111-2222-3333-4444-555555555555";
    private static readonly TenantId Tenant = new("tenant-1");

    private static IdentityRealm ScimRealm(bool allowUpn = false, bool requireCorrelation = true) => new(
        "realm-1",
        IdentityRealmKind.EntraWorkforce,
        TenantBindingSpecification.Fixed(Tenant),
        RealmGroupSource.Token,
        RealmProvisioningMode.Scim,
        issuers: ["https://issuer.example.org"])
    {
        ScimSettings = new RealmScimSettings(AllowUpnCorrelation: allowUpn, RequireCorrelation: requireCorrelation)
    };

    private static ScimUser User(
        string id,
        string? externalId = null,
        string userName = "alice",
        bool active = true,
        DateTimeOffset? deactivatedAt = null,
        string? objectSid = null,
        string? immutableId = null,
        string? upn = null,
        string? entraOid = null) =>
        new(
            Id: id,
            ExternalId: externalId ?? id,
            UserName: userName,
            DisplayName: userName,
            Email: $"{userName}@example.com",
            Active: active,
            DeactivatedAt: deactivatedAt,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            ObjectSid: objectSid,
            OnPremisesImmutableId: immutableId,
            UserPrincipalName: upn,
            EntraObjectId: entraOid);

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Bearer"));

    [Fact]
    public async Task Onprem_sid_correlates()
    {
        var store = new InMemoryCorrelationStore();
        store.Users.Add(User("u-1", objectSid: SidA));
        var audit = new RecordingAuditWriter();
        var sut = new ScimIdentityCorrelator(store, store, audit);

        var result = await sut.CorrelateAsync(
            Principal(new Claim("onprem_sid", SidA)),
            ScimRealm(),
            Tenant);

        result.Status.ShouldBe(CorrelationStatus.Matched);
        result.ScimUserKey.ShouldBe("u-1");
        result.MatchedByKey.ShouldBe("objectSid");
        audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Immutable_id_correlates()
    {
        var store = new InMemoryCorrelationStore();
        store.Users.Add(User("u-imm", immutableId: ImmutableA));
        var sut = new ScimIdentityCorrelator(store, store);

        var result = await sut.CorrelateAsync(
            Principal(new Claim("onPremisesImmutableId", ImmutableA)),
            ScimRealm(),
            Tenant);

        result.Status.ShouldBe(CorrelationStatus.Matched);
        result.ScimUserKey.ShouldBe("u-imm");
        result.MatchedByKey.ShouldBe("onPremisesImmutableId");
    }

    [Fact]
    public async Task Oid_correlates_via_externalId()
    {
        var store = new InMemoryCorrelationStore();
        store.Users.Add(User("u-oid", externalId: OidA, entraOid: null));
        var sut = new ScimIdentityCorrelator(store, store);

        var result = await sut.CorrelateAsync(
            Principal(new Claim("oid", OidA)),
            ScimRealm(),
            Tenant);

        result.Status.ShouldBe(CorrelationStatus.Matched);
        result.ScimUserKey.ShouldBe("u-oid");
        result.MatchedByKey.ShouldBe("entraObjectId");
    }

    [Fact]
    public async Task Upn_ignored_unless_enabled()
    {
        var store = new InMemoryCorrelationStore();
        store.Users.Add(User("u-upn", userName: "alice@example.com", upn: "alice@example.com"));
        var sut = new ScimIdentityCorrelator(store, store);

        var disabled = await sut.CorrelateAsync(
            Principal(new Claim("upn", "Alice@Example.com")),
            ScimRealm(allowUpn: false),
            Tenant);

        disabled.Status.ShouldBe(CorrelationStatus.NotProvisioned);

        var enabled = await sut.CorrelateAsync(
            Principal(new Claim("upn", "Alice@Example.com")),
            ScimRealm(allowUpn: true),
            Tenant);

        enabled.Status.ShouldBe(CorrelationStatus.Matched);
        enabled.ScimUserKey.ShouldBe("u-upn");
        enabled.MatchedByKey.ShouldBe("userPrincipalName");
    }

    [Fact]
    public async Task Keys_matching_different_users_are_ambiguous()
    {
        var store = new InMemoryCorrelationStore();
        store.Users.Add(User("u-1", objectSid: SidA));
        store.Users.Add(User("u-2", entraOid: OidA));
        var audit = new RecordingAuditWriter();
        var sut = new ScimIdentityCorrelator(store, store, audit);

        var result = await sut.CorrelateAsync(
            Principal(new Claim("onprem_sid", SidA), new Claim("oid", OidA)),
            ScimRealm(),
            Tenant);

        result.Status.ShouldBe(CorrelationStatus.Ambiguous);
        audit.Entries.Count.ShouldBe(1);
        audit.Entries[0].EventType.ShouldBe(AuditEventTypes.IdentityCorrelationAmbiguous);
        audit.Entries[0].Decision.ShouldBe("DENY");
        audit.Entries[0].TargetTable.ShouldBe("IDENTITY_USERS");
        audit.Entries[0].DetailsJson!.ShouldNotContain(SidA);
        audit.Entries[0].DetailsJson!.ShouldNotContain(OidA);
    }

    [Fact]
    public async Task Key_matching_two_users_is_ambiguous()
    {
        var store = new InMemoryCorrelationStore();
        store.Users.Add(User("u-1", externalId: OidA));
        store.Users.Add(User("u-2", entraOid: OidA));
        var audit = new RecordingAuditWriter();
        var sut = new ScimIdentityCorrelator(store, store, audit);

        var result = await sut.CorrelateAsync(
            Principal(new Claim("oid", OidA)),
            ScimRealm(),
            Tenant);

        result.Status.ShouldBe(CorrelationStatus.Ambiguous);
        audit.Entries.Count.ShouldBe(1);
        audit.Entries[0].EventType.ShouldBe(AuditEventTypes.IdentityCorrelationAmbiguous);
        audit.Entries[0].DetailsJson!.ShouldNotContain(OidA);
    }

    [Fact]
    public async Task Unmatched_stronger_key_with_matching_weaker_key_is_ambiguous()
    {
        var store = new InMemoryCorrelationStore();
        store.Users.Add(User("u-weaker", entraOid: OidA, upn: "alice@example.com"));
        var audit = new RecordingAuditWriter();
        var sut = new ScimIdentityCorrelator(store, store, audit);

        var result = await sut.CorrelateAsync(
            Principal(new Claim("onprem_sid", SidB), new Claim("oid", OidA)),
            ScimRealm(),
            Tenant);

        result.Status.ShouldBe(CorrelationStatus.Ambiguous);
        audit.Entries.Count.ShouldBe(1);
        audit.Entries[0].EventType.ShouldBe(AuditEventTypes.IdentityCorrelationAmbiguous);
        audit.Entries[0].DetailsJson!.ShouldNotContain(SidB);
        audit.Entries[0].DetailsJson!.ShouldNotContain(OidA);
    }

    [Fact]
    public async Task Not_provisioned_is_degraded_when_required()
    {
        var store = new InMemoryCorrelationStore();
        var correlator = new ScimIdentityCorrelator(store, store);
        var options = Options.Create(new GatewayOptions { Authentication = new AuthenticationOptions() });
        var resolver = new GroupSidResolver(
            null,
            [],
            options,
            scimCorrelator: correlator,
            scimGroupStore: store,
            scimProvisioningStore: store);

        var requiredOutcome = await resolver.ResolveAsync(
            Principal(new Claim("oid", OidA), new Claim("groups", SidA)),
            ScimRealm(requireCorrelation: true),
            Tenant,
            "Bearer",
            CancellationToken.None);

        requiredOutcome.Status.ShouldBe(GroupResolutionStatus.Degraded);
        requiredOutcome.GroupSids.ShouldBeEmpty();

        var optionalOutcome = await resolver.ResolveAsync(
            Principal(new Claim("oid", OidA), new Claim("groups", SidA)),
            ScimRealm(requireCorrelation: false),
            Tenant,
            "Bearer",
            CancellationToken.None);

        optionalOutcome.Status.ShouldBe(GroupResolutionStatus.Complete);
        optionalOutcome.GroupSids.ShouldContain(new Sid(SidA));
    }

    [Fact]
    public async Task Inactive_user_is_forbidden()
    {
        var store = new InMemoryCorrelationStore();
        store.Users.Add(User("u-off", objectSid: SidA, active: false, deactivatedAt: DateTimeOffset.UtcNow));
        var correlator = new ScimIdentityCorrelator(store, store);

        var corr = await correlator.CorrelateAsync(
            Principal(new Claim("onprem_sid", SidA)),
            ScimRealm(),
            Tenant);

        corr.Status.ShouldBe(CorrelationStatus.Deprovisioned);
        corr.ScimUserKey.ShouldBe("u-off");

        var options = Options.Create(new GatewayOptions { Authentication = new AuthenticationOptions() });
        var resolver = new GroupSidResolver(
            null,
            [],
            options,
            scimCorrelator: correlator,
            scimGroupStore: store,
            scimProvisioningStore: store);

        var resolved = await resolver.ResolveAsync(
            Principal(new Claim("onprem_sid", SidA), new Claim("groups", SidB)),
            ScimRealm(),
            Tenant,
            "Bearer",
            CancellationToken.None);

        resolved.Status.ShouldBe(GroupResolutionStatus.Degraded);
        resolved.GroupSids.ShouldBeEmpty();
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryCorrelationStore : IIdentityStoreRepository
    {
        public List<ScimUser> Users { get; } = [];
        public HashSet<string> Locks { get; } = new(StringComparer.Ordinal);

        public Task<ScimUser?> GetUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) =>
            Task.FromResult(Users.Find(u => u.UserKey == userKey));

        public Task<ScimUser?> GetUserByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) =>
            Task.FromResult(Users.Find(u => u.ExternalId == externalId));

        public Task<ScimUser?> GetUserByUserNameAsync(TenantId tenantId, string realmId, string userName, CancellationToken ct = default) =>
            Task.FromResult(Users.Find(u => string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase)));

        public Task<ScimUser?> GetUserByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default) =>
            Task.FromResult(Users.Find(u => u.ObjectSid == objectSid));

        public Task<ScimUser?> GetUserByImmutableIdAsync(TenantId tenantId, string realmId, string onPremisesImmutableId, CancellationToken ct = default) =>
            Task.FromResult(Users.Find(u => u.OnPremisesImmutableId == onPremisesImmutableId));

        public Task<ScimUser?> GetUserByEntraObjectIdAsync(TenantId tenantId, string realmId, string entraObjectId, CancellationToken ct = default) =>
            Task.FromResult(Users.Find(u => u.EntraObjectId == entraObjectId));

        public Task<ScimUser?> GetUserByUpnAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default) =>
            Task.FromResult(Users.Find(u => string.Equals(u.UserPrincipalName, userPrincipalName, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<ScimUser>> GetUsersByEntraObjectIdOrExternalIdAsync(TenantId tenantId, string realmId, string identifier, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScimUser>>(
                Users.FindAll(u => string.Equals(u.EntraObjectId, identifier, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(u.ExternalId, identifier, StringComparison.Ordinal)));

        public Task<IReadOnlyList<ScimUser>> GetUsersByUpnOrUserNameAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScimUser>>(
                Users.FindAll(u => string.Equals(u.UserPrincipalName, userPrincipalName, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(u.UserName, userPrincipalName, StringComparison.OrdinalIgnoreCase)));

        public Task<bool> IsSubjectLockedAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) =>
            Task.FromResult(Locks.Contains(userKey));

        public Task<EffectiveScimGroupsResult> GetEffectiveGroupSidsForUserAsync(TenantId tenantId, string realmId, string userKey, int maxDepth = 32, CancellationToken ct = default) =>
            Task.FromResult(new EffectiveScimGroupsResult(new HashSet<string>(), false));

        public Task<ScimRealmSyncState?> GetRealmSyncStateAsync(TenantId tenantId, string realmId, CancellationToken ct = default) =>
            Task.FromResult<ScimRealmSyncState?>(null);

        public Task UpsertUserAsync(ScimUser user, TenantId tenantId, string realmId, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ScimUser>> ListUsersAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimUser>>([]);
        public Task<ScimGroup?> GetGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => Task.FromResult<ScimGroup?>(null);
        public Task<ScimGroup?> GetGroupByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) => Task.FromResult<ScimGroup?>(null);
        public Task<ScimGroup?> GetGroupByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default) => Task.FromResult<ScimGroup?>(null);
        public Task UpsertGroupAsync(ScimGroup group, TenantId tenantId, string realmId, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> DeleteGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<ScimGroup>> ListGroupsAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimGroup>>([]);
        public Task AddGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetGroupMembershipsForUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> GetGroupMembersAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task AddGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetChildGroupIdsAsync(TenantId tenantId, string realmId, string parentGroupId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<ScimGroupNestingEdge>> ListGroupNestingEdgesAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimGroupNestingEdge>>([]);
        public Task AddLockAsync(ScimLock lockEntry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ScimLock>> GetLocksAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimLock>>([]);
        public Task<ScimLockWindow> CountLocksSinceAsync(TenantId tenantId, string realmId, IReadOnlyList<string> sources, DateTimeOffset sinceUtc, int cap, CancellationToken ct = default) => Task.FromResult(new ScimLockWindow(0, null));
        public Task RemoveLockAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertRealmSyncStateAsync(TenantId tenantId, string realmId, DateTimeOffset lastContactAt, DateTimeOffset? lastWriteAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> IncrementRealmDeprovisionEpochAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
