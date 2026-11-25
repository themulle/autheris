using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Services;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Messaging;
using Autheris.Tests.Unit.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

[Trait("Category", "Security")]
public sealed class GovernanceWriteInvalidatorTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InvalidateTablesAsync_BumpsTableEpoch_LogsTableKey_InvokesMetadata_AndNotifiesTenantCaches()
    {
        var bus = new InProcessChannelEventBus();
        using var epochs = new EpochValidationService(eventBus: bus);
        var notifier = new RecordingChangeNotifier();
        var cacheTarget = new RecordingCacheInvalidator();
        var table = new TableIdentifier("lake", "sales", "orders");
        var invalidatedMetadata = new List<string>();
        var invalidator = new GovernanceWriteInvalidator(epochs, notifier, () => [cacheTarget], bus);

        var genBefore = epochs.InvalidationGeneration!.Value;
        await invalidator.InvalidateTablesAsync(
            new TenantId("acme"),
            [table],
            metadataInvalidator: key => invalidatedMetadata.Add(key));

        invalidatedMetadata.ShouldBe(["lake.sales.orders"]);
        var logSet = epochs.GetInvalidatedSince(genBefore);
        logSet.ShouldNotBeNull();
        logSet.ShouldContain("lake.sales.orders");
        (await epochs.GetCurrentEpochAsync(table)).ShouldBeGreaterThan(1L);
        notifier.Events.Count.ShouldBe(1);
        notifier.Events[0].TenantId.ShouldBe("acme");
        notifier.Events[0].IsFullInvalidation.ShouldBeFalse();
        cacheTarget.Tenants.ShouldBe(["acme"]);
    }

    [Fact]
    public async Task InvalidateWideAsync_AppendsAllTablesMarker_PublishesProfileChannel_AndInvalidatesCaches()
    {
        var bus = new InProcessChannelEventBus();
        using var epochs = new EpochValidationService(eventBus: bus);
        var notifier = new RecordingChangeNotifier();
        var cacheTarget = new RecordingCacheInvalidator();
        var profileInvalidations = new List<string>();
        using var sub = bus.Subscribe<string>(AccessProfileCache.InvalidationChannel, tenant =>
        {
            profileInvalidations.Add(tenant);
            return Task.CompletedTask;
        });

        var invalidator = new GovernanceWriteInvalidator(epochs, notifier, () => [cacheTarget], bus);
        var genBefore = epochs.InvalidationGeneration!.Value;

        await invalidator.InvalidateWideAsync(
            new TenantId("acme"),
            catalogVersion: new CatalogVersion(2, 5),
            governanceEpoch: 17,
            invalidateAccessProfiles: true);

        epochs.InvalidationGeneration!.Value.ShouldBeGreaterThan(genBefore);
        epochs.GetInvalidatedSince(genBefore).ShouldBeNull("InvalidateAllTables forces full cache rebuild.");
        profileInvalidations.ShouldBe(["acme"]);
        notifier.Events.Count.ShouldBe(1);
        notifier.Events[0].Epoch.ShouldBe(17);
        notifier.Events[0].TenantId.ShouldBe("acme");
        cacheTarget.Tenants.ShouldBe(["acme"]);
    }

    [Fact]
    public async Task InvalidateWideAsync_WithInvalidateAccessProfiles_EvictsAccessProfileCacheEntry()
    {
        var bus = new InProcessChannelEventBus();
        var notifier = new RedisGovernanceChangeChannel();
        var tenant = new TenantId("acme");
        var loads = 0;
        var repo = NullFake.Create<IAccessProfileRepository>((m, _) =>
        {
            if (m.Name == nameof(IAccessProfileRepository.GetProfilesForSubjectAsync))
            {
                loads++;
                return Task.FromResult<IReadOnlyList<AccessProfile>>([]);
            }

            return NullFake.UseDefault;
        });

        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10_000 });
        using var profileCache = new AccessProfileCache(repo, clusterState: null, memoryCache, eventBus: bus, changeNotifier: notifier);

        await profileCache.GetProfilesAsync(tenant, "S-1-5-21-1-1-1-1001");
        loads.ShouldBe(1);

        var invalidator = new GovernanceWriteInvalidator(changeNotifier: notifier, cacheInvalidators: () => [profileCache], eventBus: bus);
        await invalidator.InvalidateWideAsync(tenant, invalidateAccessProfiles: true);

        await profileCache.GetProfilesAsync(tenant, "S-1-5-21-1-1-1-1001");
        loads.ShouldBe(2);
    }

    [Fact]
    public async Task InvalidateTablesAsync_WithReservedPlatformTenant_BroadcastsFullInvalidationInsteadOfThrowing()
    {
        var notifier = new RecordingChangeNotifier();
        var cacheTarget = new RecordingCacheInvalidator();
        var channel = new RedisGovernanceChangeChannel();
        var invalidator = new GovernanceWriteInvalidator(changeNotifier: notifier, cacheInvalidators: () => [cacheTarget]);

        await invalidator.InvalidateTablesAsync(
            TenantId.Platform,
            [new TableIdentifier("lake", "sales", "orders")]);

        notifier.Events.Count.ShouldBe(1);
        notifier.Events[0].IsFullInvalidation.ShouldBeTrue();
        cacheTarget.AllInvalidations.ShouldBe(1);
        await channel.NotifyAsync(notifier.Events[0]);
    }

    [Fact]
    public async Task CatalogAccessViewCache_InvalidationViaWideWrite_DropsCachedDecision()
    {
        var bus = new InProcessChannelEventBus();
        using var epochs = new EpochValidationService(eventBus: bus);
        var version = new CatalogVersion(1, 1);
        var versions = new FixedCatalogVersionProvider(() => version);
        using var cache = new CatalogAccessViewCache(null, epochs, versions, null);
        var tenant = new TenantId("acme");

        var stampBefore = await cache.ReadStampAsync(tenant, CancellationToken.None);
        stampBefore.ShouldNotBeNull();
        var entry = new AccessViewEntry(
            ReadGrantCandidates.Empty,
            System.Collections.Frozen.FrozenDictionary<TableIdentifier, bool>.Empty,
            stampBefore!,
            DateTimeOffset.UtcNow.AddMinutes(5));
        (await cache.TrySetAsync("k", tenant, entry, CancellationToken.None)).ShouldBeTrue();

        var invalidator = new GovernanceWriteInvalidator(epochs, eventBus: bus);
        await invalidator.InvalidateWideAsync(tenant);

        var stampAfter = await cache.ReadStampAsync(tenant, CancellationToken.None);
        stampAfter.ShouldNotBeNull();
        cache.TryGet("k", stampAfter!).ShouldBeNull();
    }

    [Fact]
    public async Task PermissionRequestEngine_DecideAsync_RoutesTableAndWideDecisionsThroughWriteInvalidator()
    {
        var recording = new RecordingWriteInvalidator();
        var table = new TableIdentifier("lake", "sales", "orders");
        var tenant = new TenantId("acme");
        var approver = new Sid("S-1-5-21-1000-1000-1000-500");
        var tableReq = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = table,
            RequesterSid = new Sid("S-1-5-21-1000-1000-1000-1001"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-1000-1000-1000-1001",
            BusinessJustification = "Reporting need for quarterly close.",
            Status = "PENDING",
            RequestedAt = FixedNow.AddHours(-1),
            RequestedValidTo = FixedNow.AddDays(30),
            TenantId = tenant,
            Version = 1,
            RequestKind = ConsentRequestKind.Data
        };
        var profileReq = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = Guid.Empty,
            TableIdentifier = default,
            RequesterSid = new Sid("S-1-5-21-1000-1000-1000-1001"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-1000-1000-1000-1001",
            BusinessJustification = "Access profile bundle assignment.",
            Status = "PENDING",
            RequestedAt = FixedNow.AddHours(-1),
            RequestedValidTo = FixedNow.AddDays(30),
            TenantId = tenant,
            Version = 1,
            RequestKind = ConsentRequestKind.ProfileAssignment,
            ProfileId = Guid.NewGuid()
        };

        var requests = new Dictionary<Guid, ConsentRequest> { [tableReq.Id] = tableReq, [profileReq.Id] = profileReq };
        var repo = NullFake.Create<IConsentApprovalRepository>((m, args) =>
        {
            if (m.Name == nameof(IConsentApprovalRepository.TryDecideAsync))
            {
                var id = (Guid)args![0]!;
                var req = requests[id];
                var updated = new ConsentRequest
                {
                    Id = req.Id,
                    TableId = req.TableId,
                    TableIdentifier = req.TableIdentifier,
                    RequesterSid = req.RequesterSid,
                    RequestedGranteeType = req.RequestedGranteeType,
                    RequestedGranteeRef = req.RequestedGranteeRef,
                    BusinessJustification = req.BusinessJustification,
                    Status = "APPROVED",
                    RequestedAt = req.RequestedAt,
                    RequestedValidTo = req.RequestedValidTo,
                    DecidedAt = FixedNow,
                    TenantId = req.TenantId,
                    Version = req.Version + 1,
                    RequestKind = req.RequestKind,
                    ProfileId = req.ProfileId
                };
                return Task.FromResult(new DecisionCasResult(DecisionCasOutcome.Applied, updated, [new GrantRef(Guid.NewGuid(), PermissionGrantKind.Data)]));
            }

            return NullFake.UseDefault;
        });

        var engine = new PermissionRequestEngine(
            repo,
            NullFake.Create<IAuditWriter>(),
            new ApproverResolver(),
            new ApproverEligibility(new GovernanceCapabilities()),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            [],
            timeProvider: new FakeTimeProvider(FixedNow),
            writeInvalidator: recording);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, approver.Value),
            new Claim("tenant_id", tenant.Value),
            new Claim(ClaimTypes.Role, "GovernanceAdmin")
        ], "Bearer"));
        var caller = new SecurityPrincipalContext
        {
            UserSid = approver,
            TenantId = tenant,
            GroupSids = new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>(["GovernanceAdmin"], StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            Principal = principal
        };

        await engine.DecideAsync(new PermissionDecisionCommand(tableReq.Id, 1, caller, true, FixedNow.AddDays(7), "Approved"));
        await engine.DecideAsync(new PermissionDecisionCommand(profileReq.Id, 1, caller, true, FixedNow.AddDays(7), "Approved"));

        recording.TableCalls.ShouldBe([(tenant, table)]);
        recording.WideCalls.ShouldBe([(tenant, true)]);
    }

    [Fact]
    public async Task AddGovernanceProtectionCaching_ResolvesInvalidatorAndInvalidatesWithoutClusterStateOrProfileCache()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton(NullFake.Create<IGovernanceEpochStore>());
        services.AddSingleton<IGovernanceCacheInvalidator>(_ =>
            throw new InvalidOperationException("Simulates AccessProfileCache without IDistributedClusterStateProvider."));

        var register = typeof(GatewayGovernanceServiceExtensions).GetMethod(
            "AddGovernanceProtectionCaching",
            BindingFlags.Static | BindingFlags.NonPublic);
        register.ShouldNotBeNull();
        register.Invoke(null, [services]);

        await using var sp = services.BuildServiceProvider();
        var invalidator = sp.GetRequiredService<IGovernanceWriteInvalidator>();
        var notifier = sp.GetRequiredService<IGovernanceChangeNotifier>();
        var observed = new List<GovernanceChangeEvent>();
        using var sub = notifier.Subscribe(observed.Add);

        await invalidator.InvalidateWideAsync(new TenantId("acme"), governanceEpoch: 9);

        observed.Count.ShouldBe(1);
        observed[0].TenantId.ShouldBe("acme");
        observed[0].Epoch.ShouldBe(9);
    }

    private sealed class RecordingChangeNotifier : IGovernanceChangeNotifier
    {
        public List<GovernanceChangeEvent> Events { get; } = [];
        public Task NotifyAsync(GovernanceChangeEvent change, CancellationToken ct = default)
        {
            Events.Add(change);
            return Task.CompletedTask;
        }
        public IDisposable Subscribe(Action<GovernanceChangeEvent> handler) => NoOpDisposable.Instance;
    }

    private sealed class RecordingCacheInvalidator : IGovernanceCacheInvalidator
    {
        public List<string> Tenants { get; } = [];
        public int AllInvalidations { get; private set; }
        public void InvalidateTenant(string tenantId) => Tenants.Add(tenantId);
        public void InvalidateAll() => AllInvalidations++;
    }

    private sealed class RecordingWriteInvalidator : IGovernanceWriteInvalidator
    {
        public List<(TenantId? Tenant, TableIdentifier Table)> TableCalls { get; } = [];
        public List<(TenantId? Tenant, bool InvalidateAccessProfiles)> WideCalls { get; } = [];

        public Task InvalidateTablesAsync(TenantId? tenant, IEnumerable<TableIdentifier> tables, CatalogVersion? catalogVersion = null, Action<string>? metadataInvalidator = null, CancellationToken ct = default)
        {
            TableCalls.AddRange(tables.Select(t => (tenant, t)));
            return Task.CompletedTask;
        }

        public Task InvalidateWideAsync(TenantId? tenant, CatalogVersion? catalogVersion = null, long governanceEpoch = 0, Guid? tableId = null, DateTimeOffset? occurredAtUtc = null, bool invalidateAccessProfiles = false, CancellationToken ct = default)
        {
            WideCalls.Add((tenant, invalidateAccessProfiles));
            return Task.CompletedTask;
        }

        public Task PublishCatalogChangedAsync(CatalogVersion version, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedCatalogVersionProvider(Func<CatalogVersion> version) : ICatalogVersionProvider
    {
        public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) => ValueTask.FromResult(version());
        public Task<string> GetVersionAsync(CancellationToken ct = default) => Task.FromResult(version().Formatted);
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public void Dispose() { }
    }
}
