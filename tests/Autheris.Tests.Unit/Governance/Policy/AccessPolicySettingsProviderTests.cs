using System.Collections.Concurrent;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Governance.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Policy;

[Trait("Category", "Security")]
public sealed class AccessPolicySettingsProviderTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid GovActor = new("S-1-5-21-1001-2002-3003-10");
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static (AccessPolicySettingsProvider Provider, FakePolicyRepo Repo, InMemoryAuditWriter Audit) Build(
        AccessPolicyOptions? deployment = null)
    {
        var repo = new FakePolicyRepo();
        var audit = new InMemoryAuditWriter();
        var provider = new AccessPolicySettingsProvider(
            repo,
            Options.Create(deployment ?? new AccessPolicyOptions()),
            audit,
            new FakeTimeProvider(T0));
        return (provider, repo, audit);
    }

    [Fact]
    public async Task Ceiling_secret_rejected()
    {
        var (provider, repo, _) = Build();

        var result = await provider.UpdateAsync(
            TenantA,
            new TenantAccessPolicyUpdate(SelfGrantCeiling: ProtectionClass.Secret),
            expectedVersion: 0,
            GovActor);

        result.Status.ShouldBe(AccessPolicyUpdateStatus.Invalid);
        result.ErrorCode.ShouldBe("SELF_GRANT_CEILING_INVALID");
        repo.UpsertCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Clamped_by_deployment()
    {
        var trustedTid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa").ToString("D");
        var extraTid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb").ToString("D");
        var allowedGroup = "S-1-5-21-100-200-300-512";
        var extraGroup = "S-1-5-21-100-200-300-999";

        var deployment = new AccessPolicyOptions
        {
            MaxDataOwnerDays = 365,
            MaxGovernanceDays = 730,
            MaxItAdminDays = 90,
            MaxSelfGrantDays = 60,
            SelfGrantCeiling = ProtectionClass.Internal,
            DefaultContainerVisibility = CatalogVisibilityMode.OnRequest,
            ServeImportSamples = false,
            QuarantineSuspectedPiiSamples = true,
            MaxBundleItems = 25,
            MaxPendingRequestsPerRequester = 40,
            MaxTablesPerAssignment = 200,
            MaxPageSize = 100,
            Internal = new InternalAccessPolicySettings
            {
                TrustedIssuerTenantIds = [trustedTid],
                EmployeeGroupSids = [allowedGroup],
                RequireMemberIndicator = true,
                TrustedServicePrincipalClientIds = ["client-allowed"]
            }
        };

        var (provider, repo, _) = Build(deployment);
        repo.Stored = new StoredTenantAccessPolicy(
            TenantA,
            new TenantAccessPolicyUpdate(
                MaxDataOwnerDays: 3650,
                MaxGovernanceDays: 3650,
                MaxItAdminDays: 365,
                MaxSelfGrantDays: 365,
                SelfGrantCeiling: ProtectionClass.Confidential,
                DefaultContainerVisibility: CatalogVisibilityMode.Open,
                Internal: new InternalAudiencePolicyUpdate(
                    TrustedIssuerTenantIds: [trustedTid, extraTid],
                    EmployeeGroupSids: [allowedGroup, extraGroup],
                    RequireMemberIndicator: false,
                    TrustedServicePrincipalClientIds: ["client-allowed", "client-extra"]),
                ServeImportSamples: true,
                QuarantineSuspectedPiiSamples: false,
                MaxBundleItems: 50,
                MaxPendingRequestsPerRequester: 100,
                MaxTablesPerAssignment: 1000,
                MaxPageSize: 500),
            Version: 2,
            UpdatedBy: GovActor.Value,
            UpdatedAt: T0);

        var effective = await provider.GetEffectiveAsync(TenantA);

        effective.MaxDataOwnerDays.ShouldBe(365);
        effective.MaxGovernanceDays.ShouldBe(730);
        effective.MaxItAdminDays.ShouldBe(90);
        effective.MaxSelfGrantDays.ShouldBe(60);
        effective.SelfGrantCeiling.ShouldBe(ProtectionClass.Internal);
        effective.DefaultContainerVisibility.ShouldBe(CatalogVisibilityMode.OnRequest);
        effective.ServeImportSamples.ShouldBeFalse();
        effective.QuarantineSuspectedPiiSamples.ShouldBeTrue();
        effective.MaxBundleItems.ShouldBe(25);
        effective.MaxPendingRequestsPerRequester.ShouldBe(40);
        effective.MaxTablesPerAssignment.ShouldBe(200);
        effective.MaxPageSize.ShouldBe(100);
        effective.Internal.RequireMemberIndicator.ShouldBeTrue();
        effective.Internal.TrustedIssuerTenantIds.ShouldBe([trustedTid]);
        effective.Internal.EmployeeGroupSids.ShouldBe([allowedGroup]);
        effective.Internal.TrustedServicePrincipalClientIds.ShouldBe(["client-allowed"]);
    }

    [Fact]
    public async Task Reminder_list_bounds()
    {
        var (provider, _, _) = Build();

        (await provider.UpdateAsync(
            TenantA,
            new TenantAccessPolicyUpdate(ReminderDays: Enumerable.Range(1, 11).ToArray()),
            0,
            GovActor)).Status.ShouldBe(AccessPolicyUpdateStatus.Invalid);

        (await provider.UpdateAsync(
            TenantA,
            new TenantAccessPolicyUpdate(ReminderDays: [0, 7]),
            0,
            GovActor)).Status.ShouldBe(AccessPolicyUpdateStatus.Invalid);

        (await provider.UpdateAsync(
            TenantA,
            new TenantAccessPolicyUpdate(ReminderDays: [30, 366]),
            0,
            GovActor)).Status.ShouldBe(AccessPolicyUpdateStatus.Invalid);

        var valid = await provider.UpdateAsync(
            TenantA,
            new TenantAccessPolicyUpdate(ReminderDays: [7, 30, 7, 1]),
            0,
            GovActor);

        valid.Status.ShouldBe(AccessPolicyUpdateStatus.Updated);
        valid.Policy!.ReminderDays.ShouldBe([30, 7, 1]);
    }

    [Fact]
    public async Task ServeImportSamples_default_on()
    {
        var (provider, _, _) = Build();

        var policy = await provider.GetEffectiveAsync(TenantA);

        policy.ServeImportSamples.ShouldBeTrue();
        policy.QuarantineSuspectedPiiSamples.ShouldBeFalse();
        policy.MaxDataOwnerDays.ShouldBe(3650);
        policy.MaxGovernanceDays.ShouldBe(3650);
        policy.MaxItAdminDays.ShouldBe(365);
        policy.MaxSelfGrantDays.ShouldBe(365);
        policy.SelfGrantCeiling.ShouldBe(ProtectionClass.Confidential);
        policy.ReminderDays.ShouldBe([30, 7]);
        policy.DefaultContainerVisibility.ShouldBe(CatalogVisibilityMode.OnRequest);
        policy.MaxBundleItems.ShouldBe(50);
        policy.MaxPendingRequestsPerRequester.ShouldBe(100);
        policy.MaxTablesPerAssignment.ShouldBe(1000);
        policy.MaxPageSize.ShouldBe(500);
        policy.Internal.RequireMemberIndicator.ShouldBeTrue();
    }

    [Fact]
    public async Task Cached_policy_reuses_epoch_entry_and_invalidates_on_update_epoch_and_notification()
    {
        var time = new FakeTimeProvider(T0);
        var repo = new FakePolicyRepo();
        var audit = new InMemoryAuditWriter();
        var epochStore = new FakeEpochStore();
        var notifier = new LocalNotifier();
        using var epochProvider = new GovernanceEpochProvider(
            epochStore,
            Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }),
            notifier,
            time);
        using var provider = new AccessPolicySettingsProvider(
            repo,
            Options.Create(new AccessPolicyOptions()),
            audit,
            time,
            urlProtector: null,
            epochProvider,
            Options.Create(new GovernanceCacheSettings { MaxTtlSeconds = 300, EpochPollSeconds = 60 }),
            notifier);

        var first = await provider.GetEffectiveAsync(TenantA);
        var second = await provider.GetEffectiveAsync(TenantA);
        repo.GetCalls.ShouldBe(1);
        second.ShouldBeSameAs(first);

        // Tenant isolation: tenant B gets its own entry and does not reuse tenant A's policy.
        var tenantB = new TenantId("tenant-b");
        repo.ByTenant[tenantB] = new StoredTenantAccessPolicy(tenantB, new TenantAccessPolicyUpdate(MaxPageSize: 123), 1, GovActor.Value, T0);
        var bPolicy = await provider.GetEffectiveAsync(tenantB);
        bPolicy.MaxPageSize.ShouldBe(123);
        repo.GetCalls.ShouldBe(2);

        // UpdateAsync invalidates tenant A synchronously without affecting tenant B.
        var updated = await provider.UpdateAsync(TenantA, new TenantAccessPolicyUpdate(MaxPageSize: 77), 0, GovActor);
        updated.Status.ShouldBe(AccessPolicyUpdateStatus.Updated);
        var callsBeforeReadA = repo.GetCalls;
        var afterUpdate = await provider.GetEffectiveAsync(TenantA);
        afterUpdate.MaxPageSize.ShouldBe(77);
        repo.GetCalls.ShouldBe(callsBeforeReadA + 1);

        var callsBeforeCachedB = repo.GetCalls;
        (await provider.GetEffectiveAsync(tenantB)).MaxPageSize.ShouldBe(123);
        repo.GetCalls.ShouldBe(callsBeforeCachedB);

        // Remote change notification invalidates tenant A immediately.
        repo.ByTenant[TenantA] = new StoredTenantAccessPolicy(TenantA, new TenantAccessPolicyUpdate(MaxPageSize: 88), 2, GovActor.Value, T0);
        await notifier.NotifyAsync(new GovernanceChangeEvent(TenantA.Value, 2, null, T0));
        (await provider.GetEffectiveAsync(TenantA)).MaxPageSize.ShouldBe(88);

        // TTL expiry reloads cleanly.
        repo.ByTenant[TenantA] = new StoredTenantAccessPolicy(TenantA, new TenantAccessPolicyUpdate(MaxPageSize: 99), 3, GovActor.Value, T0);
        time.Advance(TimeSpan.FromSeconds(301));
        (await provider.GetEffectiveAsync(TenantA)).MaxPageSize.ShouldBe(99);
    }

    [Fact]
    public async Task Repository_failure_is_not_cached_and_propagates_fail_closed()
    {
        var time = new FakeTimeProvider(T0);
        var repo = new FakePolicyRepo { Failure = new InvalidOperationException("db down") };
        var audit = new InMemoryAuditWriter();
        var epochStore = new FakeEpochStore();
        var notifier = new LocalNotifier();
        using var epochProvider = new GovernanceEpochProvider(
            epochStore,
            Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }),
            notifier,
            time);
        using var provider = new AccessPolicySettingsProvider(
            repo,
            Options.Create(new AccessPolicyOptions()),
            audit,
            time,
            urlProtector: null,
            epochProvider,
            Options.Create(new GovernanceCacheSettings { MaxTtlSeconds = 300, EpochPollSeconds = 60 }),
            notifier);

        await Should.ThrowAsync<InvalidOperationException>(() => provider.GetEffectiveAsync(TenantA));

        repo.Failure = null;
        var recovered = await provider.GetEffectiveAsync(TenantA);
        recovered.MaxPageSize.ShouldBe(500);
        repo.GetCalls.ShouldBe(2);
    }

    private sealed class FakePolicyRepo : IAccessPolicySettingsRepository
    {
        public Dictionary<TenantId, StoredTenantAccessPolicy> ByTenant { get; } = [];
        public StoredTenantAccessPolicy? Stored
        {
            get => ByTenant.Values.FirstOrDefault();
            set
            {
                ByTenant.Clear();
                if (value is not null)
                {
                    ByTenant[value.Tenant] = value;
                }
            }
        }

        public int GetCalls { get; private set; }
        public int UpsertCalls { get; private set; }
        public Exception? Failure { get; set; }

        public Task<StoredTenantAccessPolicy?> GetAsync(TenantId tenant, CancellationToken ct = default)
        {
            GetCalls++;
            if (Failure is not null)
            {
                return Task.FromException<StoredTenantAccessPolicy?>(Failure);
            }

            return Task.FromResult(ByTenant.GetValueOrDefault(tenant));
        }

        public Task<AccessPolicyWriteResult> UpsertAsync(TenantId tenant, TenantAccessPolicyUpdate settings, int expectedVersion, Sid actor, CancellationToken ct = default)
        {
            UpsertCalls++;
            var stored = new StoredTenantAccessPolicy(tenant, settings, expectedVersion + 1, actor.Value, T0);
            ByTenant[tenant] = stored;
            return Task.FromResult(new AccessPolicyWriteResult(
                expectedVersion == 0 ? AccessPolicyWriteStatus.Created : AccessPolicyWriteStatus.Updated,
                stored));
        }

        public Task<bool> DeleteIfVersionAsync(TenantId tenant, int expectedVersion, CancellationToken ct = default)
        {
            if (ByTenant.TryGetValue(tenant, out var existing) && existing.Version == expectedVersion)
            {
                ByTenant.Remove(tenant);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }

    private sealed class FakeEpochStore : IGovernanceEpochStore
    {
        private readonly ConcurrentDictionary<string, long> _epochs = new(StringComparer.Ordinal);

        public Task<long> GetEpochAsync(TenantId tenant, CancellationToken ct = default) =>
            Task.FromResult(_epochs.GetValueOrDefault(tenant.Value, 1));

        public Task<long> BumpEpochAsync(TenantId tenant, CancellationToken ct = default) =>
            Task.FromResult(_epochs.AddOrUpdate(tenant.Value, 2, static (_, v) => v + 1));
    }

    private sealed class LocalNotifier : IGovernanceChangeNotifier
    {
        private readonly List<Action<GovernanceChangeEvent>> _handlers = [];

        public Task NotifyAsync(GovernanceChangeEvent change, CancellationToken ct = default)
        {
            foreach (var h in _handlers.ToArray())
            {
                h(change);
            }

            return Task.CompletedTask;
        }

        public IDisposable Subscribe(Action<GovernanceChangeEvent> handler)
        {
            _handlers.Add(handler);
            return new Sub(() => _handlers.Remove(handler));
        }

        private sealed class Sub(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }
}
