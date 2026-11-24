namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Visibility;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Discovery single-object reads (REST <c>/api/v1/catalog/objects/{id}</c>, <c>/api/v1/catalog/datasets/{id}</c>) apply the container
/// visibility rule on top of the tenant scope: a table outside the caller's tenant scope is still found when the tenant's own container it
/// is assigned to is <c>Open</c>, catalog-granted to the caller or owned by the caller. Anything else answers exactly like a missing table.
/// </summary>
[Trait("Category", "Security")]
public sealed class DiscoveryContainerVisibilityTests
{
    private const string Caller = "S-1-5-21-7001-1001";
    private const string Other = "S-1-5-21-7001-1002";
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly ContainerRef OpenDomain = new(ContainerKind.Domain, Guid.NewGuid());
    private static readonly ContainerRef ClosedDomain = new(ContainerKind.Domain, Guid.NewGuid());
    private static readonly ContainerRef GrantedDomain = new(ContainerKind.Domain, Guid.NewGuid());
    private static readonly ContainerRef UnassignedDomain = new(ContainerKind.Domain, Guid.NewGuid());
    private static readonly ContainerRef ForeignDomain = new(ContainerKind.Domain, Guid.NewGuid());

    [Fact]
    public async Task Open_container_table_outside_the_tenant_scope_is_found()
    {
        var (service, table) = Build("in_open", OpenDomain);

        (await service.GetAsync(table, CallerOf(Caller))).ShouldNotBeNull();
    }

    [Fact]
    public async Task OnRequest_container_table_without_a_grant_answers_like_a_missing_one()
    {
        var (service, table) = Build("in_closed", ClosedDomain);

        (await service.GetAsync(table, CallerOf(Caller))).ShouldBeNull();
        (await service.GetAsync(new TableIdentifier("dom", "dbo", "missing"), CallerOf(Caller))).ShouldBeNull();
    }

    [Fact]
    public async Task Catalog_grant_on_the_container_finds_the_table_for_the_grantee_only()
    {
        var (service, table) = Build("in_granted", GrantedDomain);

        (await service.GetAsync(table, CallerOf(Caller))).ShouldNotBeNull();
        (await service.GetAsync(table, CallerOf(Other))).ShouldBeNull();
    }

    [Fact]
    public async Task Open_system_unassigned_container_does_not_widen_the_tenant_scope()
    {
        var (service, table) = Build("in_unassigned", UnassignedDomain);

        (await service.GetAsync(table, CallerOf(Caller))).ShouldBeNull();
    }

    [Fact]
    public async Task Container_of_another_tenant_does_not_widen_the_tenant_scope()
    {
        var (service, table) = Build("in_foreign", ForeignDomain);

        (await service.GetAsync(table, CallerOf(Caller))).ShouldBeNull();
    }

    [Fact]
    public async Task Table_owned_by_another_tenant_stays_out_of_scope_in_an_open_container()
    {
        var (service, table) = Build("foreign_owned", OpenDomain, ownerTenant: "tenant-b");

        (await service.GetAsync(table, CallerOf(Caller))).ShouldBeNull();
    }

    [Fact]
    public async Task Unavailable_snapshot_admits_nothing()
    {
        var (service, table) = Build("in_open", OpenDomain, snapshotAvailable: false);

        (await service.GetAsync(table, CallerOf(Caller))).ShouldBeNull();
    }

    [Fact]
    public async Task Protection_failure_admits_nothing()
    {
        var (service, table) = Build("in_open", OpenDomain, protectionFails: true);

        (await service.GetAsync(table, CallerOf(Caller))).ShouldBeNull();
    }

    private static CatalogCaller CallerOf(string sid) =>
        DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User(sid, TenantA.Value), TenantA);

    private static (DiscoveryService Service, TableIdentifier Table) Build(
        string name, ContainerRef container, string? ownerTenant = null, bool snapshotAvailable = true, bool protectionFails = false)
    {
        var tables = new FakeDiscoveryTableRepository();
        var meta = DiscoveryTestBuilder.MakeTable("dom", name, ownerTenantId: ownerTenant);
        tables.Tables.Add(meta);
        var service = new DiscoveryService(
            tables,
            FakeAccessDecisionPoint.AllowAll(),
            new FakeDiscoveryConsentRepository(),
            new FakeDiscoveryApprovalRepository(),
            new FakeDiscoveryOwnershipRepository(),
            new FakeDiscoveryArtifactStore(),
            new FakeAuditWriter(),
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            Options.Create(new CatalogDiscoveryOptions()),
            logger: NullLogger<DiscoveryService>.Instance,
            protectionProvider: new StubProtection(container, protectionFails),
            containerVisibility: new StubContainers(snapshotAvailable));
        return (service, meta.Identifier);
    }

    private static EffectiveProtection ProtectionOf(ContainerRef container) => new(
        container,
        OwnerGroupSid: null,
        ProtectionClass.Internal,
        ProtectionClass.Internal,
        TableMaskCategory: null,
        new Dictionary<string, ColumnProtection>(),
        CatalogVisibilityMode.OnRequest);

    private sealed class StubProtection(ContainerRef container, bool fails) : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            fails ? throw new InvalidOperationException("protection store down") : ValueTask.FromResult(ProtectionOf(container));
    }

    private sealed class StubContainers(bool available) : IContainerVisibilitySnapshotProvider
    {
        public ValueTask<ContainerVisibilitySnapshot> GetAsync(TenantId tenant, ClaimsPrincipal principal, CancellationToken ct = default)
        {
            if (!available || tenant != TenantA)
            {
                return ValueTask.FromResult(ContainerVisibilitySnapshot.Unavailable());
            }

            var containers = new Dictionary<ContainerRef, ContainerInfo>
            {
                [OpenDomain] = new(OpenDomain, "open", null, null, CatalogVisibilityMode.Open, null),
                [ClosedDomain] = new(ClosedDomain, "closed", null, null, CatalogVisibilityMode.OnRequest, null),
                [GrantedDomain] = new(GrantedDomain, "granted", null, null, CatalogVisibilityMode.OnRequest, null),
                [UnassignedDomain] = new(UnassignedDomain, "Unassigned", null, null, CatalogVisibilityMode.Open, null, IsSystemUnassigned: true),
            };
            var granted = new HashSet<ContainerRef>();
            if (principal.GetUserSid() is { } sid && sid.Value == Caller)
            {
                granted.Add(GrantedDomain);
            }

            return ValueTask.FromResult(new ContainerVisibilitySnapshot(
                containers, granted, new HashSet<ContainerRef>(), new HashSet<TableIdentifier>(), isGovernance: false, isItAdmin: false, groupsComplete: true));
        }
    }
}
