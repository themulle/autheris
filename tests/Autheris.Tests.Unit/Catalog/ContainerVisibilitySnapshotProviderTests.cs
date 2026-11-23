namespace Autheris.Tests.Unit.Catalog;

using System.Collections.Concurrent;
using System.Security.Claims;
using Autheris.Application.Catalog.Visibility;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Governance;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ContainerVisibilitySnapshotProviderTests
{
    private static readonly TenantId Tenant = Wp0.TenantA;
    private static readonly Sid Group = new("S-1-5-21-111-222-333-4001");

    private sealed class Harness : IDisposable
    {
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
        public FakeContainerDomainRepository Domains { get; } = new();
        public FakeCatalogGrantRepository Grants { get; } = new();
        public FakeConsentRepository Consents { get; } = new();
        public Notifier ChangeNotifier { get; } = new();
        public GovernanceEpochProvider Epochs { get; }
        public ContainerVisibilitySnapshotProvider Sut { get; }

        public Harness(bool governance = false, bool itAdmin = false)
        {
            Epochs = new GovernanceEpochProvider(
                Domains, Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }), ChangeNotifier, Time);
            Sut = new ContainerVisibilitySnapshotProvider(
                Domains,
                Grants,
                Consents,
                new FakeGovernanceCapabilities(governance, itAdmin),
                Epochs,
                Options.Create(new GovernanceCacheSettings { MaxTtlSeconds = 300, NegativeTtlSeconds = 60, EpochPollSeconds = 60 }),
                ChangeNotifier,
                Time);
        }

        public void Dispose()
        {
            Sut.Dispose();
            Epochs.Dispose();
        }
    }

    private static ClaimsPrincipal User(string sid = "S-1-5-21-1001", Sid? group = null) =>
        Wp0.User(sid, Tenant.Value, extra: group is { } g
            ?
            [
                new Claim(ClaimTypes.GroupSid, g.Value, ClaimValueTypes.String, IdentityClaimIssuers.Pipeline),
                GroupResolutionClaims.CreateMarker(),
                GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete)
            ]
            : []);

    private static ClaimsPrincipal Degraded(string sid = "S-1-5-21-1001") =>
        Wp0.User(sid, Tenant.Value, extra:
        [
            GroupResolutionClaims.CreateMarker(),
            GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Degraded)
        ]);

    [Fact]
    public async Task Open_container_visible_and_on_request_not()
    {
        using var h = new Harness();
        var open = h.Domains.Add(Tenant, "open", CatalogVisibilityMode.Open);
        var closed = h.Domains.Add(Tenant, "closed", CatalogVisibilityMode.OnRequest);

        var snapshot = await h.Sut.GetAsync(Tenant, User());

        snapshot.IsAvailable.ShouldBeTrue();
        snapshot.IsContainerVisible(new ContainerRef(ContainerKind.Domain, open.Id)).ShouldBeTrue();
        snapshot.IsContainerVisible(new ContainerRef(ContainerKind.Domain, closed.Id)).ShouldBeFalse();
        snapshot.AccessState(new ContainerRef(ContainerKind.Domain, open.Id)).ShouldBe("OPEN");
        snapshot.AccessState(new ContainerRef(ContainerKind.Domain, closed.Id)).ShouldBe("NONE");
    }

    [Fact]
    public async Task Catalog_grant_of_user_or_current_group_makes_container_visible()
    {
        using var h = new Harness();
        var closed = h.Domains.Add(Tenant, "closed", CatalogVisibilityMode.OnRequest);
        var reference = new ContainerRef(ContainerKind.Domain, closed.Id);
        h.Grants.Add(Tenant, reference, Group);

        (await h.Sut.GetAsync(Tenant, User())).IsContainerVisible(reference).ShouldBeFalse();
        var member = await h.Sut.GetAsync(Tenant, User(group: Group));

        member.IsContainerVisible(reference).ShouldBeTrue();
        member.AccessState(reference).ShouldBe("GRANTED");
    }

    [Fact]
    public async Task Owner_group_member_sees_own_container()
    {
        using var h = new Harness();
        var owned = h.Domains.Add(Tenant, "owned", CatalogVisibilityMode.OnRequest, owner: Group);
        var reference = new ContainerRef(ContainerKind.Domain, owned.Id);

        (await h.Sut.GetAsync(Tenant, User())).IsContainerVisible(reference).ShouldBeFalse();
        (await h.Sut.GetAsync(Tenant, User(group: Group))).IsContainerVisible(reference).ShouldBeTrue();
    }

    [Fact]
    public async Task Tables_with_active_data_grants_are_listed_and_governance_sees_everything()
    {
        using var h = new Harness();
        var closed = h.Domains.Add(Tenant, "closed", CatalogVisibilityMode.OnRequest);
        var reference = new ContainerRef(ContainerKind.Domain, closed.Id);
        var table = new TableIdentifier("sales", "dbo", "orders");
        await h.Consents.CreateConsentAsync(Wp0.Allow(table, "S-1-5-21-1001", Tenant, "id"));

        var snapshot = await h.Sut.GetAsync(Tenant, User());

        snapshot.DataGrantedTables.ShouldContain(table);
        snapshot.IsTableVisible(reference, table).ShouldBeTrue();
        snapshot.IsTableVisible(reference, new TableIdentifier("sales", "dbo", "other")).ShouldBeFalse();

        using var governance = new Harness(governance: true);
        var all = await governance.Sut.GetAsync(Tenant, User());
        all.SeesEverything.ShouldBeTrue();
        all.IsContainerVisible(reference).ShouldBeTrue();
    }

    [Fact]
    public async Task Failure_resolves_to_nothing_visible()
    {
        using var h = new Harness();
        var open = h.Domains.Add(Tenant, "open", CatalogVisibilityMode.Open);
        h.Domains.ListFailure = new InvalidOperationException("store down");

        var snapshot = await h.Sut.GetAsync(Tenant, User());

        snapshot.IsAvailable.ShouldBeFalse();
        snapshot.IsContainerVisible(new ContainerRef(ContainerKind.Domain, open.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task Degraded_groups_yield_no_grants_and_no_ownership()
    {
        using var h = new Harness();
        var owned = h.Domains.Add(Tenant, "owned", CatalogVisibilityMode.OnRequest, owner: new Sid("S-1-5-21-1001"));
        var reference = new ContainerRef(ContainerKind.Domain, owned.Id);
        h.Grants.Add(Tenant, reference, new Sid("S-1-5-21-1001"));

        var snapshot = await h.Sut.GetAsync(Tenant, Degraded());

        snapshot.GroupsComplete.ShouldBeFalse();
        snapshot.IsContainerVisible(reference).ShouldBeFalse();
        h.Grants.ListCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Cached_until_epoch_changes_and_revoke_invalidates()
    {
        using var h = new Harness();
        var closed = h.Domains.Add(Tenant, "closed", CatalogVisibilityMode.OnRequest);
        var reference = new ContainerRef(ContainerKind.Domain, closed.Id);
        h.Grants.Add(Tenant, reference, new Sid("S-1-5-21-1001"));

        (await h.Sut.GetAsync(Tenant, User())).IsContainerVisible(reference).ShouldBeTrue();
        (await h.Sut.GetAsync(Tenant, User())).IsContainerVisible(reference).ShouldBeTrue();
        h.Grants.ListCalls.ShouldBe(1);

        h.Grants.RevokeAll();
        await h.ChangeNotifier.NotifyAsync(new GovernanceChangeEvent(Tenant.Value, 1, null, DateTimeOffset.UtcNow));

        (await h.Sut.GetAsync(Tenant, User())).IsContainerVisible(reference).ShouldBeFalse();
    }

    [Fact]
    public async Task Foreign_tenant_containers_never_appear()
    {
        using var h = new Harness();
        h.Domains.Add(Wp0.TenantB, "foreign", CatalogVisibilityMode.Open);

        var snapshot = await h.Sut.GetAsync(Tenant, User());

        snapshot.Containers.ShouldBeEmpty();
    }

    private sealed class Notifier : IGovernanceChangeNotifier
    {
        private readonly ConcurrentDictionary<Guid, Action<GovernanceChangeEvent>> _handlers = new();

        public Task NotifyAsync(GovernanceChangeEvent change, CancellationToken ct = default)
        {
            foreach (var handler in _handlers.Values)
            {
                handler(change);
            }

            return Task.CompletedTask;
        }

        public IDisposable Subscribe(Action<GovernanceChangeEvent> handler)
        {
            var id = Guid.NewGuid();
            _handlers.TryAdd(id, handler);
            return new Token(() => _handlers.TryRemove(id, out _));
        }

        private sealed class Token(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
