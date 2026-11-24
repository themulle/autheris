namespace Autheris.Tests.Unit.Catalog;

using System.Security.Claims;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Catalog.Visibility;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Policy.Protection;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #1066 gap 1: the container visibility rule of <see cref="CatalogVisibilityService"/> only admits tables explicitly assigned to a
/// container of the caller's own tenant. The system <c>Unassigned</c> domain never widens visibility, even when a tenant sets it to
/// <c>Open</c>; a table owned by another tenant or resolved to a container outside the caller's tenant is never added; an unavailable
/// snapshot or any failure admits nothing.
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogContainerVisibilityScopeTests
{
    private const string UserSid = "S-1-5-21-1001";
    private static readonly TenantId Tenant = Wp0.TenantA;
    private static readonly TenantId Other = Wp0.TenantB;
    private static readonly TableIdentifier Global = new("sales", "dbo", "global_table");
    private static readonly TableIdentifier Foreign = new("sales", "dbo", "foreign_table");
    private static readonly TableIdentifier Own = new("sales", "dbo", "own_table");

    private sealed class Fixture : IDisposable
    {
        public FakeContainerDomainRepository Domains { get; } = new();
        public FakeCatalogGrantRepository Grants { get; } = new();
        public Dictionary<TableIdentifier, EffectiveProtection> Protections { get; } = [];
        public GovernanceEpochProvider Epochs { get; }
        public ContainerVisibilitySnapshotProvider Provider { get; }
        public IUnmaskGrantResolver? Unmask { get; set; }

        public Fixture(bool governance = false, bool itAdmin = false)
        {
            Epochs = new GovernanceEpochProvider(Domains, Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }));
            Provider = new ContainerVisibilitySnapshotProvider(
                Domains,
                Grants,
                new Autheris.Tests.Unit.Governance.FakeConsentRepository(),
                new FakeGovernanceCapabilities(governance, itAdmin),
                Epochs);
        }

        public CatalogVisibilityService Service(IContainerVisibilitySnapshotProvider? provider = null, IProtectionSnapshotProvider? protection = null) => new(
            Wp0.Tables(
                Wp0.Table("sales", "global_table", "Global", ["id"]),
                Wp0.Table("sales", "foreign_table", "Foreign", ["id"]) with { OwnerTenantId = Other.Value },
                Wp0.Table("sales", "own_table", "Own", ["id"]) with { OwnerTenantId = Tenant.Value }),
            Wp0.Consents(),
            Options.Create(Wp0.Options()),
            NullLogger<CatalogVisibilityService>.Instance,
            protectionProvider: protection ?? new StubProtection(Protections),
            containerVisibility: provider ?? Provider,
            unmaskResolver: Unmask);

        public void AssignAll(GovernanceDomain domain, string? maskCategory = null)
        {
            foreach (var id in new[] { Global, Foreign, Own })
            {
                Protections[id] = Protection(new ContainerRef(ContainerKind.Domain, domain.Id), domain.Visibility, maskCategory);
            }
        }

        public void Dispose()
        {
            Provider.Dispose();
            Epochs.Dispose();
        }
    }

    private static EffectiveProtection Protection(ContainerRef container, CatalogVisibilityMode visibility, string? maskCategory = null) => new(
        container,
        OwnerGroupSid: null,
        ProtectionClass.Confidential,
        ProtectionClass.Confidential,
        TableMaskCategory: maskCategory,
        new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase),
        visibility);

    private static CatalogCaller Caller() => CatalogVisibilityTestFactory.Caller(Wp0.User(UserSid, Tenant.Value));

    [Fact]
    public async Task Open_unassigned_domain_exposes_nothing()
    {
        using var f = new Fixture();
        var unassigned = f.Domains.Add(Tenant, "Unassigned", CatalogVisibilityMode.Open, unassigned: true);
        f.AssignAll(unassigned);
        var service = f.Service();

        (await service.GetVisibleAsync(Caller())).Tables.ShouldBeEmpty();
        (await service.FindVisibleAsync(Caller(), Global)).ShouldBeNull();
        (await service.FilterVisibleAsync(Caller(), [Global, Own])).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Governance_or_it_capacity_never_widens_through_the_unassigned_domain(bool governance, bool itAdmin)
    {
        using var f = new Fixture(governance, itAdmin);
        var unassigned = f.Domains.Add(Tenant, "Unassigned", CatalogVisibilityMode.Open, unassigned: true);
        f.AssignAll(unassigned);

        (await f.Service().GetVisibleAsync(Caller())).Tables.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unmask_grant_never_widens_through_the_unassigned_domain()
    {
        using var f = new Fixture();
        var unassigned = f.Domains.Add(Tenant, "Unassigned", CatalogVisibilityMode.OnRequest, unassigned: true);
        f.AssignAll(unassigned, maskCategory: "PII");
        f.Unmask = new WholeTableUnmask();

        (await f.Service().GetVisibleAsync(Caller())).Tables.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("granted")]
    [InlineData("owned")]
    public async Task Catalog_grant_or_ownership_never_widens_through_the_unassigned_domain(string path)
    {
        using var f = new Fixture();
        var unassigned = f.Domains.Add(
            Tenant, "Unassigned", CatalogVisibilityMode.Open, owner: path == "owned" ? new Sid(UserSid) : (Sid?)null, unassigned: true);
        if (path == "granted")
        {
            f.Grants.Add(Tenant, new ContainerRef(ContainerKind.Domain, unassigned.Id), new Sid(UserSid));
        }

        f.AssignAll(unassigned);

        (await f.Service().GetVisibleAsync(Caller())).Tables.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("open")]
    [InlineData("granted")]
    [InlineData("owned")]
    public async Task Own_open_granted_or_owned_container_still_admits_its_tenant_scoped_tables(string path)
    {
        using var f = new Fixture();
        var domain = f.Domains.Add(
            Tenant,
            "sales-domain",
            path == "open" ? CatalogVisibilityMode.Open : CatalogVisibilityMode.OnRequest,
            owner: path == "owned" ? new Sid(UserSid) : (Sid?)null);
        if (path == "granted")
        {
            f.Grants.Add(Tenant, new ContainerRef(ContainerKind.Domain, domain.Id), new Sid(UserSid));
        }

        f.AssignAll(domain);
        var service = f.Service();

        var visible = await service.GetVisibleAsync(Caller());
        visible.Tables.Select(t => t.Identifier).ShouldBe([Global, Own], ignoreOrder: true);
        visible.Tables.ShouldAllBe(t => t.CatalogView == CatalogView.ContainerVisible);
        (await service.FindVisibleAsync(Caller(), Global)).ShouldNotBeNull();
        (await service.FindVisibleAsync(Caller(), Foreign)).ShouldBeNull();
    }

    [Fact]
    public async Task On_request_container_without_grant_or_ownership_admits_nothing()
    {
        using var f = new Fixture();
        var domain = f.Domains.Add(Tenant, "closed", CatalogVisibilityMode.OnRequest);
        f.AssignAll(domain);

        (await f.Service().GetVisibleAsync(Caller())).Tables.ShouldBeEmpty();
    }

    [Fact]
    public async Task Another_tenants_table_is_never_added_even_in_an_open_own_container()
    {
        using var f = new Fixture();
        var open = f.Domains.Add(Tenant, "open", CatalogVisibilityMode.Open);
        f.AssignAll(open);

        var visible = await f.Service().GetVisibleAsync(Caller());

        visible.Tables.Select(t => t.Identifier).ShouldBe([Global, Own], ignoreOrder: true);
        visible.Tables.ShouldAllBe(t => t.CatalogView == CatalogView.ContainerVisible);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Table_assigned_to_a_container_outside_the_callers_tenant_is_never_added(bool governance, bool itAdmin)
    {
        using var f = new Fixture(governance, itAdmin);
        f.Domains.Add(Tenant, "own-open", CatalogVisibilityMode.Open);
        var foreignOpen = f.Domains.Add(Other, "foreign-open", CatalogVisibilityMode.Open);
        f.AssignAll(foreignOpen);

        (await f.Service().GetVisibleAsync(Caller())).Tables.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unavailable_snapshot_admits_nothing_even_with_governance_capacity()
    {
        using var f = new Fixture();
        var open = f.Domains.Add(Tenant, "open", CatalogVisibilityMode.Open);
        f.AssignAll(open);
        var unavailable = new FixedSnapshot(ContainerVisibilitySnapshot.Unavailable(isGovernance: true, isItAdmin: true));

        (await f.Service(unavailable).GetVisibleAsync(Caller())).Tables.ShouldBeEmpty();
    }

    [Fact]
    public async Task Snapshot_provider_exception_admits_nothing()
    {
        using var f = new Fixture();
        var open = f.Domains.Add(Tenant, "open", CatalogVisibilityMode.Open);
        f.AssignAll(open);

        (await f.Service(new ThrowingSnapshot()).GetVisibleAsync(Caller())).Tables.ShouldBeEmpty();
    }

    [Fact]
    public async Task Protection_lookup_exception_admits_nothing()
    {
        using var f = new Fixture(governance: true);
        f.Domains.Add(Tenant, "open", CatalogVisibilityMode.Open);

        (await f.Service(protection: new StubProtection([])).GetVisibleAsync(Caller())).Tables.ShouldBeEmpty();
    }

    private sealed class StubProtection(Dictionary<TableIdentifier, EffectiveProtection> map) : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            map.TryGetValue(table, out var value) ? ValueTask.FromResult(value) : throw new InvalidOperationException("unknown table");
    }

    private sealed class FixedSnapshot(ContainerVisibilitySnapshot snapshot) : IContainerVisibilitySnapshotProvider
    {
        public ValueTask<ContainerVisibilitySnapshot> GetAsync(TenantId tenant, ClaimsPrincipal principal, CancellationToken ct = default) =>
            ValueTask.FromResult(snapshot);
    }

    private sealed class ThrowingSnapshot : IContainerVisibilitySnapshotProvider
    {
        public ValueTask<ContainerVisibilitySnapshot> GetAsync(TenantId tenant, ClaimsPrincipal principal, CancellationToken ct = default) =>
            throw new InvalidOperationException("snapshot store down");
    }

    private sealed class WholeTableUnmask : IUnmaskGrantResolver
    {
        public ValueTask<UnmaskCoverage> ResolveAsync(
            TenantId tenant, IReadOnlyCollection<Sid> subjects, bool groupsComplete, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(new UnmaskCoverage(wholeTable: true, new HashSet<string>()));
    }
}
