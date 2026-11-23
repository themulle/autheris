namespace Autheris.Tests.Unit.Catalog;

using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Catalog.Visibility;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>Container tiles behind <c>GET /api/v1/catalog/containers</c>, <c>/containers/{kind}/{id}</c> and <c>/domains</c>.</summary>
public sealed class CatalogContainerEndpointsTests
{
    private static readonly TenantId Tenant = Wp0.TenantA;
    private const string UserSid = "S-1-5-21-1001";

    private sealed class Fixture : IDisposable
    {
        public FakeContainerDomainRepository Domains { get; } = new();
        public FakeCatalogGrantRepository Grants { get; } = new();
        public Autheris.Tests.Unit.Governance.FakeConsentRepository Consents { get; } = new();
        public GovernanceEpochProvider Epochs { get; }
        public ContainerVisibilitySnapshotProvider Provider { get; }

        public Fixture()
        {
            Epochs = new GovernanceEpochProvider(Domains, Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }));
            Provider = new ContainerVisibilitySnapshotProvider(Domains, Grants, Consents, new FakeGovernanceCapabilities(), Epochs);
        }

        public ContainerCatalogService Service(int maxPageSize = 500) => new(Provider, maxPageSize: maxPageSize);

        public void Dispose()
        {
            Provider.Dispose();
            Epochs.Dispose();
        }
    }

    private static ClaimsPrincipal User() => Wp0.User(UserSid, Tenant.Value);

    [Fact]
    public async Task Tiles_carry_name_description_contact_visibility_and_access_state()
    {
        using var f = new Fixture();
        var open = f.Domains.Add(Tenant, "finance", CatalogVisibilityMode.Open);
        var closed = f.Domains.Add(Tenant, "hr", CatalogVisibilityMode.OnRequest);
        var granted = f.Domains.Add(Tenant, "legal", CatalogVisibilityMode.OnRequest);
        f.Grants.Add(Tenant, new ContainerRef(ContainerKind.Domain, granted.Id), new Sid(UserSid));

        var tiles = await f.Service().ListAsync(Tenant, User(), kind: null, limit: null);

        tiles.Select(t => t.Name).ShouldBe(["finance", "hr", "legal"]);
        tiles.Single(t => t.Id == open.Id).AccessState.ShouldBe("OPEN");
        tiles.Single(t => t.Id == closed.Id).AccessState.ShouldBe("NONE");
        tiles.Single(t => t.Id == granted.Id).AccessState.ShouldBe("GRANTED");
        tiles.Single(t => t.Id == open.Id).Visibility.ShouldBe("OPEN");
        tiles.Single(t => t.Id == closed.Id).Visibility.ShouldBe("ON_REQUEST");
        tiles.Single(t => t.Id == open.Id).Description.ShouldBe("finance description");
        tiles.Single(t => t.Id == open.Id).Contact.ShouldNotBeNull();
    }

    [Fact]
    public async Task System_unassigned_domain_and_foreign_tenant_containers_are_not_listed()
    {
        using var f = new Fixture();
        f.Domains.Add(Tenant, "Unassigned", CatalogVisibilityMode.OnRequest, unassigned: true);
        f.Domains.Add(Wp0.TenantB, "foreign", CatalogVisibilityMode.Open);
        f.Domains.Add(Tenant, "own", CatalogVisibilityMode.Open);

        var tiles = await f.Service().ListAsync(Tenant, User(), null, null);

        tiles.Select(t => t.Name).ShouldBe(["own"]);
    }

    [Fact]
    public async Task Page_size_is_bounded()
    {
        using var f = new Fixture();
        for (var i = 0; i < 12; i++)
        {
            f.Domains.Add(Tenant, $"d{i:D2}", CatalogVisibilityMode.Open);
        }

        (await f.Service(maxPageSize: 5).ListAsync(Tenant, User(), null, limit: 1000)).Count.ShouldBe(5);
        (await f.Service().ListAsync(Tenant, User(), null, limit: 3)).Count.ShouldBe(3);
        (await f.Service().ListAsync(Tenant, User(), null, limit: -4)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Single_tile_by_kind_and_id_and_unknown_is_null()
    {
        using var f = new Fixture();
        var open = f.Domains.Add(Tenant, "finance", CatalogVisibilityMode.Open);

        (await f.Service().GetAsync(Tenant, User(), ContainerKind.Domain, open.Id))!.Name.ShouldBe("finance");
        (await f.Service().GetAsync(Tenant, User(), ContainerKind.DataProduct, open.Id)).ShouldBeNull();
        (await f.Service().GetAsync(Tenant, User(), ContainerKind.Domain, Guid.NewGuid())).ShouldBeNull();
    }

    [Fact]
    public async Task Store_failure_lists_nothing()
    {
        using var f = new Fixture();
        f.Domains.Add(Tenant, "finance", CatalogVisibilityMode.Open);
        f.Domains.ListFailure = new InvalidOperationException("store down");

        (await f.Service().ListAsync(Tenant, User(), null, null)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task No_oracle_on_columns()
    {
        using var f = new Fixture();
        f.Domains.Add(Tenant, "hr", CatalogVisibilityMode.OnRequest);

        var tiles = await f.Service().ListAsync(Tenant, User(), null, null);
        var json = JsonSerializer.Serialize(tiles);

        // A tile reveals container metadata only: no table, column, class or mask information.
        using var document = JsonDocument.Parse(json);
        var names = document.RootElement[0].EnumerateObject().Select(p => p.Name).ToList();
        names.ShouldBe(["Kind", "Id", "Name", "Description", "Contact", "Visibility", "AccessState"]);
    }
}
