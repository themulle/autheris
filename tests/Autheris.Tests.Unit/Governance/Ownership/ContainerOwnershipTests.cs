namespace Autheris.Tests.Unit.Governance.Ownership;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Security;
using Autheris.Application.Governance.Ownership;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
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

        public Task<ContainerDefaults> GetContainerForTableAsync(TenantId tenant, Guid tableId, CancellationToken ct = default)
        {
            SeenTenant = tenant;
            return Throw ? throw new InvalidOperationException("store down") : Task.FromResult(Container);
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

    private static ContainerOwnership Create(StubDomains domains, TableMetadata? meta, IScimGroupStore? groups = null, ITenantResolver? resolver = null) =>
        new(meta is null ? new InMemoryTableMetadataRepository() : new InMemoryTableMetadataRepository(meta), domains, groups, null, resolver);

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

    [Fact]
    public async Task Tenant_falls_back_to_default_tenant_and_without_tenant_nothing_is_owned()
    {
        var domains = new StubDomains { Container = Assigned(OwnerGroup) };
        var withDefault = new TenantResolver(() => new TenancyOptions { DefaultTenantId = "tenant-d" }, null, NullLogger<TenantResolver>.Instance);

        (await Create(domains, Meta(ownerTenant: null), GroupsOf(OwnerGroup.Value), withDefault).IsAuthorizedApproverForTableAsync(Orders, Member)).ShouldBeTrue();
        domains.SeenTenant.ShouldBe(new TenantId("tenant-d"));
        (await Create(domains, Meta(ownerTenant: null), GroupsOf(OwnerGroup.Value)).IsAuthorizedApproverForTableAsync(Orders, Member)).ShouldBeFalse();
    }
}
