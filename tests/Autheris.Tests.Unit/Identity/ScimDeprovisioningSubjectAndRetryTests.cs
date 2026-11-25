namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Identity.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Tests.Unit.Security;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ScimDeprovisioningSubjectAndRetryTests
{
    private static readonly TenantId Tenant = new("tenant-w5");
    private const string Realm = "workforce-entra";
    private const string UserKey = "user-key-42";
    private const string ObjectSid = "S-1-5-21-1000-2000-3000-4242";
    private const string EntraObjectId = "11111111-2222-3333-4444-555555555542";
    private const string ExternalId = "ext-user-42";

    [Fact]
    public async Task DeprovisionAndLockSubjectAsync_LoadsUserBeforeDeactivation_AndRevokesAllCorrelatedSubjectForms()
    {
        var user = new ScimUser(
            Id: UserKey,
            ExternalId: ExternalId,
            UserName: "alice@example.com",
            DisplayName: "Alice",
            Email: "alice@example.com",
            Active: true,
            DeactivatedAt: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            ObjectSid: ObjectSid,
            EntraObjectId: EntraObjectId);
        var state = new FakeIdentityState { User = user };
        var revocation = new RecordingRevocation();
        var sut = new IdentityDeprovisioningService(CreateStore(state), revocation);

        await sut.DeprovisionAndLockSubjectAsync(Tenant, Realm, UserKey, "SCIM_DEPROVISION", "SCIM_PUT");

        var getIdx = state.Calls.IndexOf("GetUser");
        var deactIdx = state.Calls.IndexOf("DeactivateUser");
        getIdx.ShouldBeGreaterThanOrEqualTo(0);
        deactIdx.ShouldBeGreaterThan(getIdx);

        revocation.Subjects.ShouldBe(
        [
            new Sid(UserKey),
            new Sid($"{Realm}|{UserKey}"),
            new Sid(ObjectSid),
            new Sid($"{Realm}|{EntraObjectId}"),
            new Sid(EntraObjectId),
            new Sid(ExternalId)
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task DeprovisionAndLockSubjectAsync_DeduplicatesOverlappingSubjectForms()
    {
        var user = new ScimUser(
            Id: UserKey,
            ExternalId: UserKey,
            UserName: "alice@example.com",
            DisplayName: "Alice",
            Email: null,
            Active: true,
            DeactivatedAt: null,
            CreatedAt: null,
            UpdatedAt: null,
            ObjectSid: ObjectSid,
            EntraObjectId: UserKey);
        var state = new FakeIdentityState { User = user };
        var revocation = new RecordingRevocation();
        var sut = new IdentityDeprovisioningService(CreateStore(state), revocation);

        await sut.DeprovisionAndLockSubjectAsync(Tenant, Realm, UserKey, "SCIM_DEPROVISION", "SCIM_PATCH");

        revocation.Subjects.Count.ShouldBe(3);
        revocation.Subjects.ShouldBe(
        [
            new Sid(UserKey),
            new Sid($"{Realm}|{UserKey}"),
            new Sid(ObjectSid)
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task DeprovisionAndLockSubjectAsync_CrossRealmAmbiguousUserKey_SkipsBareUserKey_ButStillRevokesObjectSidAndRealmQualifiedEntraId()
    {
        var user = new ScimUser(
            Id: UserKey,
            ExternalId: UserKey,
            UserName: "alice@example.com",
            DisplayName: "Alice",
            Email: null,
            Active: true,
            DeactivatedAt: null,
            CreatedAt: null,
            UpdatedAt: null,
            ObjectSid: ObjectSid,
            EntraObjectId: EntraObjectId);
        var state = new FakeIdentityState { User = user, UserKeyExistsInOtherRealm = true };
        var revocation = new RecordingRevocation();
        var sut = new IdentityDeprovisioningService(CreateStore(state), revocation);

        await sut.DeprovisionAndLockSubjectAsync(Tenant, Realm, UserKey, "SCIM_DEPROVISION", "SCIM_PUT");

        revocation.Subjects.ShouldNotContain(new Sid(UserKey));
        revocation.Subjects.ShouldContain(new Sid($"{Realm}|{UserKey}"));
        revocation.Subjects.ShouldContain(new Sid(ObjectSid));
        revocation.Subjects.ShouldContain(new Sid($"{Realm}|{EntraObjectId}"));
        revocation.Subjects.ShouldContain(new Sid(EntraObjectId));
    }

    [Fact]
    public async Task PutDeactivationRetry_WhenUserAlreadyInactive_ReRunsDeprovisionAndLockIdempotently()
    {
        var user = new ScimUser(
            Id: UserKey,
            ExternalId: ExternalId,
            UserName: "alice@example.com",
            DisplayName: "Alice",
            Email: null,
            Active: true,
            DeactivatedAt: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
        var state = new FakeIdentityState { User = user };
        var deprov = new RetryableDeprovisionStub(state) { FailNext = true };
        var scim = new ScimService(CreateStore(state), deprov);

        var inactivePayload = user with { Active = false };

        await Should.ThrowAsync<GrantRevocationIncompleteException>(() =>
            scim.UpdateUserAsync(Tenant, Realm, UserKey, inactivePayload));
        state.User!.Active.ShouldBeFalse();
        deprov.Calls.ShouldBe(1);

        deprov.FailNext = false;
        var retried = await scim.UpdateUserAsync(Tenant, Realm, UserKey, inactivePayload);
        retried.ShouldNotBeNull();
        retried!.Active.ShouldBeFalse();
        deprov.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task PatchDeactivationRetry_WhenUserAlreadyInactive_ReRunsDeprovisionAndLockIdempotently()
    {
        var user = new ScimUser(
            Id: UserKey,
            ExternalId: ExternalId,
            UserName: "alice@example.com",
            DisplayName: "Alice",
            Email: null,
            Active: true,
            DeactivatedAt: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
        var state = new FakeIdentityState { User = user };
        var deprov = new RetryableDeprovisionStub(state) { FailNext = true };
        var scim = new ScimService(CreateStore(state), deprov);

        var patch = new ScimPatchRequest(
            [ScimSchemaUris.PatchOp],
            [new ScimPatchOperation("replace", "active", false)]);

        await Should.ThrowAsync<GrantRevocationIncompleteException>(() =>
            scim.PatchUserAsync(Tenant, Realm, UserKey, patch));
        state.User!.Active.ShouldBeFalse();
        deprov.Calls.ShouldBe(1);

        deprov.FailNext = false;
        var retried = await scim.PatchUserAsync(Tenant, Realm, UserKey, patch);
        retried.ShouldNotBeNull();
        retried!.Active.ShouldBeFalse();
        deprov.Calls.ShouldBe(2);
    }

    private static IIdentityStoreRepository CreateStore(FakeIdentityState state) =>
        NullFake.Create<IIdentityStoreRepository>((m, args) =>
        {
            switch (m.Name)
            {
                case nameof(IScimUserStore.GetUserAsync):
                    state.Calls.Add("GetUser");
                    return Task.FromResult(state.User);
                case nameof(IScimUserStore.DeactivateUserAsync):
                    state.Calls.Add("DeactivateUser");
                    if (state.User is not null)
                    {
                        state.User = state.User with { Active = false, DeactivatedAt = DateTimeOffset.UtcNow };
                    }

                    return Task.CompletedTask;
                case nameof(IScimUserStore.UserKeyExistsInOtherRealmAsync):
                    return Task.FromResult(state.UserKeyExistsInOtherRealm);
                case nameof(IScimProvisioningStore.AddLockAsync):
                    state.Locked = true;
                    return Task.CompletedTask;
                case nameof(IScimProvisioningStore.IsSubjectLockedAsync):
                    return Task.FromResult(state.Locked);
                default:
                    return NullFake.UseDefault;
            }
        });

    private sealed class FakeIdentityState
    {
        public ScimUser? User { get; set; }
        public bool UserKeyExistsInOtherRealm { get; set; }
        public bool Locked { get; set; }
        public List<string> Calls { get; } = [];
    }

    private sealed class RecordingRevocation : IGrantRevocationService
    {
        public List<Sid> Subjects { get; } = [];

        public Task<GrantRevocationResult> RevokeAllForSubjectAsync(TenantId tenant, Sid subject, string reason, CancellationToken ct = default)
        {
            Subjects.Add(subject);
            return Task.FromResult(new GrantRevocationResult(0, 0, 0, 0, false));
        }
    }

    private sealed class RetryableDeprovisionStub(FakeIdentityState state) : IIdentityDeprovisioningService
    {
        public bool FailNext { get; set; }
        public int Calls { get; private set; }

        public Task DeprovisionAndLockSubjectAsync(TenantId tenantId, string realmId, string userKey, string reason, string source, CancellationToken ct = default)
        {
            Calls++;
            state.Locked = true;
            if (state.User is not null)
            {
                state.User = state.User with { Active = false, DeactivatedAt = DateTimeOffset.UtcNow };
            }

            if (FailNext)
            {
                throw new GrantRevocationIncompleteException(new GrantRevocationResult(0, 0, 0, 1, false));
            }

            return Task.CompletedTask;
        }

        public Task DeprovisionAsync(TenantId tenantId, string realmId, string userKey, string reason, Sid actorSid, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
