namespace Autheris.Tests.Unit.Governance.Ownership;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Security;
using Autheris.Application.Governance.Ownership;
using Autheris.Application.Identity;
using Autheris.Domain.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Fakes;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ContainerOwnershipTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");
    private static readonly Sid OwnerGroup = new("S-1-5-21-1-2-3-500");
    private static readonly Sid Member = new("S-1-5-21-1-2-3-1001");

    private sealed class StubDomains : IGovernanceDomainRepository
    {
        public ContainerDefaults Container { get; set; } = ContainerDefaults.Unassigned;
        public bool Throw { get; set; }
        public TenantId? SeenTenant { get; private set; }

        public int SingleCalls { get; private set; }
        public int BatchCalls { get; private set; }

        public Task<ContainerDefaults> GetContainerForTableAsync(TenantId tenant, Guid tableId, CancellationToken ct = default)
        {
            SingleCalls++;
            SeenTenant = tenant;
            return Throw ? throw new InvalidOperationException("store down") : Task.FromResult(Container);
        }

        public Task<IReadOnlyDictionary<Guid, ContainerDefaults>> GetContainersForTablesAsync(
            TenantId tenant, IReadOnlyCollection<Guid> tableIds, CancellationToken ct = default)
        {
            BatchCalls++;
            SeenTenant = tenant;
            if (Throw)
            {
                throw new InvalidOperationException("store down");
            }

            IReadOnlyDictionary<Guid, ContainerDefaults> result = tableIds.ToDictionary(id => id, _ => Container);
            return Task.FromResult(result);
        }

        public Task<GovernanceDomain?> GetAsync(TenantId tenant, Guid domainId, CancellationToken ct = default) =>
            Task.FromResult<GovernanceDomain?>(null);
        public Task<IReadOnlyList<GovernanceDomain>> ListAsync(TenantId tenant, int limit = 100, int offset = 0, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<GovernanceDomainUpsertResult> UpsertAsync(GovernanceDomain domain, int expectedVersion, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> DeleteAsync(TenantId tenant, Guid domainId, int expectedVersion, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<GovernanceDomain> EnsureUnassignedAsync(TenantId tenant, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AssignTablesAsync(TenantId tenant, Guid domainId, IReadOnlyList<Guid> tableIds, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<(ObjectProtection? Table, IReadOnlyDictionary<string, ObjectProtection> Columns)> GetProtectionAsync(
            TenantId tenant, Guid tableId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetProtectionAsync(TenantId tenant, Guid tableId, string column, ObjectProtection protection, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SetTableProtectionAsync(
            TenantId tenant, Guid tableId, ObjectProtection protection, IReadOnlyList<string>? clearColumns, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static TableMetadata Meta(string? ownerTenant = "tenant-a", bool active = true) => new()
    {
        Identifier = Orders,
        OwnerTenantId = ownerTenant,
        Table = new Table { TableName = "orders", SchemaName = "public", IsActive = active }
    };

    private static ContainerDefaults Assigned(Sid? owner) =>
        new(new ContainerRef(ContainerKind.Domain, Guid.NewGuid()), owner, ProtectionClass.Internal, CatalogVisibilityMode.OnRequest);

    private static IScimGroupStore GroupsOf(params string[] groupSids)
    {
        var store = Substitute.For<IScimGroupStore>();
        store.GetEffectiveGroupSidsForUserAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EffectiveScimGroupsResult(new HashSet<string>(groupSids), false));
        return store;
    }

    private static IIdentityRealmRegistry Realms(params string[] realmIds)
    {
        var registry = Substitute.For<IIdentityRealmRegistry>();
        registry.GetAllRealms().Returns(realmIds.Select(id => new IdentityRealm(id, IdentityRealmKind.EntraWorkforce, TenantBindingSpecification.Fixed(Tenant))).ToList());
        return registry;
    }

    private static ContainerOwnership Create(StubDomains domains, TableMetadata? meta, IScimGroupStore? groups = null, IIdentityRealmRegistry? realms = null) =>
        new(meta is null ? new InMemoryTableMetadataRepository() : new InMemoryTableMetadataRepository(meta), domains, groups, realms ?? Realms("realm-1"));

    [Fact]
    public async Task Unassigned_table_has_no_owner()
    {
        var domains = new StubDomains();
        var sut = Create(domains, Meta(), GroupsOf(OwnerGroup.Value));

        (await sut.IsAuthorizedApproverForTableAsync(Orders, Member)).ShouldBeFalse();
        (await sut.GetDataOwnersForTableAsync(Orders)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_table_has_no_owner()
    {
        var domains = new StubDomains { Container = Assigned(OwnerGroup) };
        var sut = Create(domains, meta: null, GroupsOf(OwnerGroup.Value));

        (await sut.IsAuthorizedApproverForTableAsync(Orders, Member)).ShouldBeFalse();
    }

    [Fact]
    public async Task Container_without_owner_group_has_no_owner()
    {
        var domains = new StubDomains { Container = Assigned(null) };
        var sut = Create(domains, Meta(), GroupsOf(OwnerGroup.Value));

        (await sut.IsAuthorizedApproverForTableAsync(Orders, Member)).ShouldBeFalse();
        (await sut.GetDataOwnersForTableAsync(Orders)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Resolution_failure_is_not_owner()
    {
        var domains = new StubDomains { Container = Assigned(OwnerGroup), Throw = true };
        var sut = Create(domains, Meta(), GroupsOf(OwnerGroup.Value));

        (await sut.IsAuthorizedApproverForTableAsync(Orders, Member)).ShouldBeFalse();
        (await sut.GetDataOwnersForTableAsync(Orders)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Degraded_groups_are_not_owner()
    {
        var domains = new StubDomains { Container = Assigned(OwnerGroup) };
        var store = Substitute.For<IScimGroupStore>();
        store.GetEffectiveGroupSidsForUserAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EffectiveScimGroupsResult(new HashSet<string> { OwnerGroup.Value }, MaxDepthExceeded: true));
        var sut = Create(domains, Meta(), store);

        (await sut.IsAuthorizedApproverForTableAsync(Orders, Member)).ShouldBeFalse();
    }

    [Fact]
    public async Task Group_store_failure_is_not_owner()
    {
        var domains = new StubDomains { Container = Assigned(OwnerGroup) };
        var store = Substitute.For<IScimGroupStore>();
        store.GetEffectiveGroupSidsForUserAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<EffectiveScimGroupsResult>(_ => throw new InvalidOperationException("down"));
        var sut = Create(domains, Meta(), store);

        (await sut.IsAuthorizedApproverForTableAsync(Orders, Member)).ShouldBeFalse();
    }

    [Fact]
    public async Task Member_of_owner_group_is_owner_and_non_member_is_not()
    {
        var domains = new StubDomains { Container = Assigned(OwnerGroup) };

        (await Create(domains, Meta(), GroupsOf(OwnerGroup.Value)).IsAuthorizedApproverForTableAsync(Orders, Member)).ShouldBeTrue();
        (await Create(domains, Meta(), GroupsOf("S-1-5-21-9-9-9-1")).IsAuthorizedApproverForTableAsync(Orders, Member)).ShouldBeFalse();
        (await Create(domains, Meta(), groups: null).IsAuthorizedApproverForTableAsync(Orders, Member)).ShouldBeFalse();
    }

    [Fact]
    public async Task Owner_group_itself_is_owner_and_blank_sid_is_not()
    {
        var domains = new StubDomains { Container = Assigned(OwnerGroup) };
        var sut = Create(domains, Meta(), groups: null);

        (await sut.IsAuthorizedApproverForTableAsync(Orders, OwnerGroup)).ShouldBeTrue();
        (await sut.IsAuthorizedApproverForTableAsync(Orders, default)).ShouldBeFalse();
    }

    [Fact]
    public async Task Owner_listing_maps_to_the_owner_group_sid()
    {
        var domains = new StubDomains { Container = Assigned(OwnerGroup) };
        var sut = Create(domains, Meta(), GroupsOf());

        var owners = await sut.GetDataOwnersForTableAsync(Orders);
        owners.Count.ShouldBe(1);
        owners[0].AdSid.ShouldBe(OwnerGroup);
        (await ((IContainerOwnership)sut).GetAuthorizedApproverSidsForTableAsync(Orders, DateTimeOffset.UtcNow)).ShouldContain(OwnerGroup.Value);
    }

    [Fact]
    public async Task Tenant_comes_from_the_target_table()
    {
        var domains = new StubDomains { Container = Assigned(OwnerGroup) };
        var sut = Create(domains, Meta(ownerTenant: "tenant-b"), GroupsOf(OwnerGroup.Value));

        (await sut.IsAuthorizedApproverForTableAsync(Orders, Member)).ShouldBeTrue();
        domains.SeenTenant.ShouldBe(new TenantId("tenant-b"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad tenant/../x")]
    [InlineData("_reserved")]
    public async Task Table_without_a_valid_owner_tenant_has_no_owner_and_never_falls_back(string? ownerTenant)
    {
        var domains = new StubDomains { Container = Assigned(OwnerGroup) };
        var sut = Create(domains, Meta(ownerTenant: ownerTenant), GroupsOf(OwnerGroup.Value));

        (await sut.IsAuthorizedApproverForTableAsync(Orders, Member)).ShouldBeFalse();
        (await sut.IsAuthorizedApproverForTableAsync(Orders, OwnerGroup)).ShouldBeFalse();
        (await sut.GetDataOwnersForTableAsync(Orders)).ShouldBeEmpty();
        domains.SeenTenant.ShouldBeNull("no container is read for a table without a valid owner tenant");
    }

    [Fact]
    public async Task Unqualified_identifier_resolves_groups_only_in_the_single_configured_realm()
    {
        var domains = new StubDomains { Container = Assigned(OwnerGroup) };
        var store = GroupsOf(OwnerGroup.Value);

        (await Create(domains, Meta(), store, Realms("realm-1")).IsAuthorizedApproverForTableAsync(Orders, Member)).ShouldBeTrue();
        await store.Received().GetEffectiveGroupSidsForUserAsync(Tenant, "realm-1", Member.Value, Arg.Any<int>(), Arg.Any<CancellationToken>());

        // Several realms (or none): an unqualified identifier is ambiguous and is never a member; no default realm is assumed.
        var ambiguous = GroupsOf(OwnerGroup.Value);
        (await Create(domains, Meta(), ambiguous, Realms("realm-1", "realm-2")).IsAuthorizedApproverForTableAsync(Orders, Member)).ShouldBeFalse();
        (await Create(domains, Meta(), ambiguous, Realms()).IsAuthorizedApproverForTableAsync(Orders, Member)).ShouldBeFalse();
        await ambiguous.DidNotReceiveWithAnyArgs().GetEffectiveGroupSidsForUserAsync(default, default!, default!, default, default);

        // A realm-qualified identifier names its realm.
        var qualified = GroupsOf(OwnerGroup.Value);
        (await Create(domains, Meta(), qualified, Realms("realm-1", "realm-2")).IsAuthorizedApproverForTableAsync(Orders, new Sid("realm-2|" + Member.Value))).ShouldBeTrue();
        await qualified.Received().GetEffectiveGroupSidsForUserAsync(Tenant, "realm-2", Member.Value, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Batch_reads_resolve_tables_containers_and_groups_once()
    {
        var other = new TableIdentifier("sales", "public", "returns");
        var third = new TableIdentifier("sales", "public", "unknown");
        var metas = new InMemoryTableMetadataRepository(Meta(), new TableMetadata
        {
            Identifier = other,
            OwnerTenantId = "tenant-a",
            Table = new Table { TableName = "returns", SchemaName = "public", IsActive = true }
        });
        var domains = new StubDomains { Container = Assigned(OwnerGroup) };
        var store = GroupsOf(OwnerGroup.Value);
        var sut = new ContainerOwnership(metas, domains, store, Realms("realm-1"));

        var owned = await ((IContainerOwnership)sut).GetOwnedOrDelegatedTablesAsync(Member, [Orders, other, third], DateTimeOffset.UtcNow);
        var owners = await ((IContainerOwnership)sut).GetDataOwnersForTablesAsync([Orders, other, third]);

        owned.ShouldBe([Orders, other], ignoreOrder: true);
        owners[Orders].ShouldHaveSingleItem().AdSid.ShouldBe(OwnerGroup);
        owners[third].ShouldBeEmpty();
        domains.BatchCalls.ShouldBe(2, "one set-based container read per batch call");
        domains.SingleCalls.ShouldBe(0, "no per-table container read");
        await store.Received(1).GetEffectiveGroupSidsForUserAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
