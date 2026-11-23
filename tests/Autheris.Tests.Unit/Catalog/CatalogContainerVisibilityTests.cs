namespace Autheris.Tests.Unit.Catalog;

using System.Collections.Concurrent;
using System.Security.Claims;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Catalog.Visibility;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Policy.Protection;
using Autheris.Application.Services;
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
/// Container visibility rule of the catalog engine: an object is visible when its container is Open, or the caller holds a catalog
/// grant on the container, a data or unmask grant on the object, or is an owner-group member. An invisible object behaves exactly like a
/// missing one, and a catalog grant never exposes data.
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogContainerVisibilityTests
{
    private const string UserSid = "S-1-5-21-1001";
    private static readonly TenantId Tenant = Wp0.TenantA;
    private static readonly TableIdentifier InOpen = new("sales", "dbo", "in_open");
    private static readonly TableIdentifier InClosed = new("sales", "dbo", "in_closed");
    private static readonly TableIdentifier Missing = new("sales", "dbo", "missing");

    private sealed class Fixture : IDisposable
    {
        public FakeContainerDomainRepository Domains { get; } = new();
        public FakeCatalogGrantRepository Grants { get; } = new();
        public Autheris.Tests.Unit.Governance.FakeConsentRepository Consents { get; } = new();
        public GovernanceDomain OpenDomain { get; }
        public GovernanceDomain ClosedDomain { get; }
        public Dictionary<TableIdentifier, EffectiveProtection> Protections { get; } = [];
        public GovernanceEpochProvider Epochs { get; }
        public ContainerVisibilitySnapshotProvider Provider { get; }
        public IUnmaskGrantResolver? Unmask { get; set; }

        public Fixture(bool governance = false)
        {
            OpenDomain = Domains.Add(Tenant, "open", CatalogVisibilityMode.Open);
            ClosedDomain = Domains.Add(Tenant, "closed", CatalogVisibilityMode.OnRequest);
            Protections[InOpen] = Protection(OpenDomain, CatalogVisibilityMode.Open);
            Protections[InClosed] = Protection(ClosedDomain, CatalogVisibilityMode.OnRequest);
            Epochs = new GovernanceEpochProvider(Domains, Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }));
            Provider = new ContainerVisibilitySnapshotProvider(
                Domains, Grants, Consents, new FakeGovernanceCapabilities(governance), Epochs);
        }

        public ContainerRef Closed => new(ContainerKind.Domain, ClosedDomain.Id);

        public CatalogVisibilityService Service(IContainerVisibilitySnapshotProvider? provider = null) => new(
            Wp0.Tables(Wp0.Table("sales", "in_open", "Open", ["id", "ssn"]), Wp0.Table("sales", "in_closed", "Closed", ["id", "ssn"])),
            Wp0.Consents(),
            Options.Create(Wp0.Options()),
            NullLogger<CatalogVisibilityService>.Instance,
            protectionProvider: new StubProtection(Protections),
            containerVisibility: provider ?? Provider,
            unmaskResolver: Unmask);

        public void Dispose()
        {
            Provider.Dispose();
            Epochs.Dispose();
        }

        private static EffectiveProtection Protection(GovernanceDomain domain, CatalogVisibilityMode visibility) => new(
            new ContainerRef(ContainerKind.Domain, domain.Id),
            OwnerGroupSid: null,
            ProtectionClass.Confidential,
            ProtectionClass.Confidential,
            TableMaskCategory: null,
            new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase),
            visibility);
    }

    private static CatalogCaller Caller(string sid = UserSid) => CatalogVisibilityTestFactory.Caller(Wp0.User(sid, Tenant.Value));

    [Fact]
    public async Task Open_container_tables_listed_without_any_grant()
    {
        using var f = new Fixture();

        var visible = await f.Service().GetVisibleAsync(Caller());

        visible.Tables.Select(t => t.Identifier).ShouldBe([InOpen]);
        visible.Tables.Single().CatalogView.ShouldBe(CatalogView.Granted);
    }

    [Fact]
    public async Task Catalog_grant_lists_tables_of_the_container_with_all_column_definitions()
    {
        using var f = new Fixture();
        f.Grants.Add(Tenant, f.Closed, new Sid(UserSid));

        var visible = await f.Service().GetVisibleAsync(Caller());

        visible.Tables.Select(t => t.Identifier).ShouldBe([InClosed, InOpen], ignoreOrder: true);
        visible.Tables.Single(t => t.Identifier == InClosed).Columns.Select(c => c.ColumnName).ShouldBe(["id", "ssn"]);
    }

    [Fact]
    public async Task Catalog_grant_does_not_expose_data()
    {
        using var f = new Fixture();
        f.Grants.Add(Tenant, f.Closed, new Sid(UserSid));
        var service = f.Service();

        var visible = await service.GetVisibleAsync(Caller());

        // Listed as metadata, but no consent exists: the access status stays NONE and the data path has no grant.
        visible.Tables.ShouldContain(t => t.Identifier == InClosed);
        CatalogVisibility.ComputeAccessStatus(InClosed, activeConsents: []).ShouldBe("NONE");
        (await f.Consents.GetAllActiveConsentsForSubjectsAsync([new Sid(UserSid)], null, null, Tenant)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Invisible_equals_missing()
    {
        using var f = new Fixture();
        var service = f.Service();

        var invisible = await service.FindVisibleAsync(Caller(), InClosed);
        var missing = await service.FindVisibleAsync(Caller(), Missing);
        var filtered = await service.FilterVisibleAsync(Caller(), [InClosed, Missing]);

        invisible.ShouldBeNull();
        missing.ShouldBeNull();
        filtered.ShouldBeEmpty();
    }

    [Fact]
    public async Task Open_container_object_found_by_single_read_and_filter()
    {
        using var f = new Fixture();
        var service = f.Service();

        (await service.FindVisibleAsync(Caller(), InOpen)).ShouldNotBeNull();
        (await service.FilterVisibleAsync(Caller(), [InOpen, InClosed])).ShouldBe([InOpen]);
    }

    [Fact]
    public async Task Snapshot_failure_adds_nothing()
    {
        using var f = new Fixture();
        f.Domains.ListFailure = new InvalidOperationException("store down");

        var visible = await f.Service().GetVisibleAsync(Caller());

        visible.Tables.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unmask_grant_makes_masked_table_visible()
    {
        using var f = new Fixture();
        f.Protections[InClosed] = f.Protections[InClosed] with { TableMaskCategory = "PII" };
        f.Unmask = new WholeTableUnmask();

        var visible = await f.Service().GetVisibleAsync(Caller());

        visible.Tables.Select(t => t.Identifier).ShouldContain(InClosed);
    }

    [Fact]
    public async Task Governance_sees_every_container_object()
    {
        using var f = new Fixture(governance: true);
        var snapshot = await f.Provider.GetAsync(Tenant, Wp0.User(UserSid, Tenant.Value));

        snapshot.IsTableVisible(f.Closed, InClosed).ShouldBeTrue();
    }

    private sealed class StubProtection(Dictionary<TableIdentifier, EffectiveProtection> map) : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            map.TryGetValue(table, out var value) ? ValueTask.FromResult(value) : throw new InvalidOperationException("unknown table");
    }

    private sealed class WholeTableUnmask : IUnmaskGrantResolver
    {
        public ValueTask<UnmaskCoverage> ResolveAsync(
            TenantId tenant, IReadOnlyCollection<Sid> subjects, bool groupsComplete, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(new UnmaskCoverage(wholeTable: true, new HashSet<string>()));
    }
}
