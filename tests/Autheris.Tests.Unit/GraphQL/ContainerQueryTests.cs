namespace Autheris.Tests.Unit.GraphQL;

using Autheris.Application.Catalog.Visibility;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Types;
using Autheris.Tests.Unit.Catalog;
using Autheris.Tests.Unit.TestSupport;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>GraphQL <c>containers</c> / <c>container</c> queries: same service and tenant derivation as REST and MCP; invisible equals missing.</summary>
[Trait("Category", "Security")]
public sealed class ContainerQueryTests : IDisposable
{
    private const string UserSid = "S-1-5-21-1001";
    private static readonly TenantId Tenant = Wp0.TenantA;

    private readonly FakeContainerDomainRepository _domains = new();
    private readonly GovernanceEpochProvider _epochs;
    private readonly ContainerVisibilitySnapshotProvider _provider;
    private readonly ContainerCatalogService _service;

    public ContainerQueryTests()
    {
        _epochs = new GovernanceEpochProvider(_domains, Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }));
        _provider = new ContainerVisibilitySnapshotProvider(
            _domains, new FakeCatalogGrantRepository(), new Autheris.Tests.Unit.Governance.FakeConsentRepository(), new FakeGovernanceCapabilities(), _epochs);
        _service = new ContainerCatalogService(_provider, maxPageSize: 2);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _epochs.Dispose();
    }

    private static IHttpContextAccessor Accessor(string? tenant = null) =>
        new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Wp0.User(UserSid, tenant ?? Tenant.Value) } };

    [Fact]
    public async Task Containers_lists_tiles_capped_by_the_service_page_size()
    {
        for (var i = 0; i < 4; i++)
        {
            _domains.Add(Tenant, $"d{i}", CatalogVisibilityMode.Open);
        }

        var tiles = await new Query().GetContainersAsync(limit: 100, containerCatalog: _service, httpContextAccessor: Accessor());

        tiles.Count.ShouldBe(2);
        tiles.ShouldAllBe(t => t.AccessState == "OPEN");
    }

    [Fact]
    public async Task Containers_of_another_tenant_are_not_listed()
    {
        _domains.Add(Wp0.TenantB, "foreign", CatalogVisibilityMode.Open);

        var tiles = await new Query().GetContainersAsync(containerCatalog: _service, httpContextAccessor: Accessor());

        tiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task Invisible_container_equals_missing_container()
    {
        var foreign = _domains.Add(Wp0.TenantB, "foreign", CatalogVisibilityMode.Open);

        var hidden = await new Query().GetContainerAsync("domain", foreign.Id, _service, Accessor());
        var missing = await new Query().GetContainerAsync("domain", Guid.NewGuid(), _service, Accessor());
        var badKind = await new Query().GetContainerAsync("nonsense", foreign.Id, _service, Accessor());

        hidden.ShouldBeNull();
        missing.ShouldBeNull();
        badKind.ShouldBeNull();
    }

    [Fact]
    public async Task Unauthenticated_caller_is_rejected()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

        await Should.ThrowAsync<GraphQLException>(async () =>
            await new Query().GetContainersAsync(containerCatalog: _service, httpContextAccessor: accessor));
    }
}
