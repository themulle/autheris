namespace Autheris.Tests.Unit.Catalog.Discovery;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

public sealed class DiscoveryTenantScopeTests
{
    private static readonly HashSet<TableIdentifier> EmptyTables = [];

    [Fact]
    [Trait("Category", "Security")]
    public void DropForeignVirtualTables_FiltersOutVirtualTablesOfOtherTenants()
    {
        var ownVirtual = DiscoveryTestBuilder.MakeTable("d", "own_v", ownerTenantId: "tenant-a", virtualTable: true);
        var foreignVirtual = DiscoveryTestBuilder.MakeTable("d", "other_v", ownerTenantId: "tenant-b", virtualTable: true);
        var regular = DiscoveryTestBuilder.MakeTable("d", "regular", ownerTenantId: "tenant-a", virtualTable: false);

        var kept = DiscoveryTenantScope.DropForeignVirtualTables([ownVirtual, foreignVirtual, regular], new TenantId("tenant-a"));

        kept.ShouldBe([ownVirtual, regular]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void IsInScope_DeniesCrossTenantTables_EvenWithAdminRole()
    {
        var tableB = DiscoveryTestBuilder.MakeTable("sales", "orders", ownerTenantId: "tenant-b");
        var shared = DiscoveryTenantScope.ParseSharedTables(null);

        DiscoveryTenantScope.IsInScope(tableB, new TenantId("tenant-a"), catalogOwner: null, shared, EmptyTables).ShouldBeFalse();
    }

    [Fact]
    public void IsInScope_AllowsSharedTablesAndCatalogOwnerTables()
    {
        var tableShared = DiscoveryTestBuilder.MakeTable("ref", "countries", ownerTenantId: null);
        var shared = DiscoveryTenantScope.ParseSharedTables(["ref.dbo.countries"]);
        var catalogOwner = new TenantId("tenant-owner");

        DiscoveryTenantScope.IsInScope(tableShared, new TenantId("tenant-a"), catalogOwner: null, shared, EmptyTables).ShouldBeTrue();
        DiscoveryTenantScope.IsInScope(tableShared, catalogOwner, catalogOwner, DiscoveryTenantScope.ParseSharedTables(null), EmptyTables).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CanSeeHiddenObject_AllowsOnlyAdminOrOwner_NeverWidenedByCatalogReadAdminScope()
    {
        var hiddenTable = DiscoveryTestBuilder.MakeTable("hr", "salaries", ownerTenantId: "tenant-a", hidden: true);
        var ownedSet = new HashSet<TableIdentifier> { hiddenTable.Identifier };

        var callerTenant = new TenantId("tenant-a");
        DiscoveryTenantScope.CanSeeHiddenObject(hiddenTable, CallerScope.Subject, callerTenant, catalogOwner: null, EmptyTables).ShouldBeFalse();
        DiscoveryTenantScope.CanSeeHiddenObject(hiddenTable, CallerScope.Subject, callerTenant, catalogOwner: null, ownedSet).ShouldBeTrue();
        DiscoveryTenantScope.CanSeeHiddenObject(hiddenTable, CallerScope.Tenant, callerTenant, catalogOwner: null, EmptyTables).ShouldBeTrue();
        DiscoveryTenantScope.CanSeeHiddenObject(hiddenTable, CallerScope.None, new TenantId("tenant-b"), catalogOwner: null, EmptyTables).ShouldBeFalse();
        DiscoveryTenantScope.CanSeeHiddenObject(hiddenTable, CallerScope.Global, new TenantId("tenant-b"), catalogOwner: null, EmptyTables).ShouldBeTrue();
    }

    [Fact]
    public async Task DiscoveryService_BatchesPdpAndOwners_InsteadOfNPlusOne()
    {
        var repo = new FakeDiscoveryTableRepository();
        for (int i = 0; i < 5; i++)
        {
            repo.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", $"t_{i}"));
        }

        var pdp = FakeAccessDecisionPoint.AllowAll();
        var owners = new CountingOwnershipRepository();
        var discovery = DiscoveryTestBuilder.CreateService(tables: repo, pdp: pdp, owners: owners);

        var page = await discovery.ListAsync(new DiscoveryQuery(), DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User("S-1", "tenant-a")));

        page.Items.Count.ShouldBe(5);
        owners.BatchOwnerCalls.ShouldBe(1);
    }

    private sealed class CountingOwnershipRepository : IContainerOwnership
    {
        public int BatchOwnerCalls { get; private set; }
        public Task<IReadOnlyList<ContainerOwnerEntry>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ContainerOwnerEntry>>([]);
        public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlySet<TableIdentifier>> GetOwnedOrDelegatedTablesAsync(Sid actorSid, IReadOnlyCollection<TableIdentifier> tables, DateTimeOffset atTime, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<TableIdentifier>>(new HashSet<TableIdentifier>());

        public Task<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>> GetDataOwnersForTablesAsync(
            IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct = default)
        {
            BatchOwnerCalls++;
            return Task.FromResult<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>>(new Dictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>());
        }
    }
}
