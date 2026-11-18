using Autheris.Application.Policy.Pdp;
using Autheris.Application.Products;
using Autheris.Application.Services;
using Autheris.Application.State;
using Autheris.Application.State.Caching.TableCache;
using Autheris.Domain.Identity;
using Autheris.Domain.Options.TableCache;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Governance.Classification;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

/// <summary>Stage 2: a caller query on a product object is routed to the cached stage-1 result, decided with the caller's product grants.</summary>
[Trait("Category", "Security")]
public sealed class ProductStageTwoTests
{
    private static readonly TableIdentifier ProductObject = ProductSource.ObjectOf(ProductHarness.Tenant, "sales", "orders_view");

    private readonly ProductHarness _h = new();
    private readonly RecordingCacheStore _store = new();
    private readonly TableCacheCatalog _catalog = new();
    private readonly ScriptedRunner _runner = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));
    private readonly IDistributedClusterStateProvider _cluster = Substitute.For<IDistributedClusterStateProvider>();

    public ProductStageTwoTests()
    {
        _cluster.TryAcquireLeaseAsync(default!, default, default).ReturnsForAnyArgs(_ => new ValueTask<IDistributedLease?>(Substitute.For<IDistributedLease>()));
    }

    private ProductLoadService Loader() => new(_h.Store, _runner, _store, _catalog, _cluster, Options.Create(new TableCacheOptions()), _h.Audit, _time);

    private async Task<DataProduct> CreateAsync(LoadSettings load)
    {
        var admin = _h.Caller(ProductHarness.Admin, admin: true);
        return await _h.Service.PutAsync(new ProductPutRequest(admin, "sales", "orders_view", DataProductKind.View, "SELECT id FROM crm.public.orders", ProductHarness.Bob, load));
    }

    [Fact]
    public async Task Caller_query_reads_only_the_cached_result()
    {
        var product = await CreateAsync(new LoadSettings(LoadMode.Schedule, Interval: TimeSpan.FromHours(1)));
        var loader = Loader();
        var loaded = await loader.LoadAsync(product);
        var callsAfterLoad = _runner.Calls;

        var decision = await new ProductResultRouter(_h.Store, loader, _catalog).RouteAsync(ProductHarness.Tenant, ProductObject);

        decision.IsCached.ShouldBeTrue();
        decision.CanSatisfyFully.ShouldBeTrue();
        decision.Metadata!.Table.ShouldBe(loaded.CacheKey);
        _catalog.TryGet(loaded.CacheKey, out var meta).ShouldBeTrue();
        decision.InternalStorageTarget.ShouldBe(meta!.ActiveViewName);
        _runner.Calls.ShouldBe(callsAfterLoad, "the query never runs the definition and never touches a base table");

        // the generic table cache router never serves a product object from anything else
        var generic = await new TableCacheQueryRouter(_catalog).EvaluateRoutingAsync(ProductObject, [], null);
        generic.CanSatisfyFully.ShouldBeFalse();
    }

    [Fact]
    public async Task A_product_of_another_tenant_or_without_a_result_is_unavailable()
    {
        var product = await CreateAsync(new LoadSettings(LoadMode.Schedule, Interval: TimeSpan.FromHours(1)));
        var router = new ProductResultRouter(_h.Store, Loader(), _catalog);

        (await Should.ThrowAsync<ProductLoadException>(() => router.RouteAsync(new TenantId("tenant-b"), ProductObject))).Code.ShouldBe(ProductErrorCodes.Unavailable);
        (await Should.ThrowAsync<ProductLoadException>(() => router.RouteAsync(ProductHarness.Tenant, ProductObject))).Code.ShouldBe(ProductErrorCodes.Unavailable);
        _runner.Calls.ShouldBe(0);
        product.Status.ShouldBe(DataProductStatus.Active);
    }

    [Fact]
    public async Task Stale_beyond_max_staleness_fails()
    {
        var product = await CreateAsync(new LoadSettings(LoadMode.Schedule, Interval: TimeSpan.FromHours(1), MaxStalenessMinutes: 30));
        var loader = Loader();
        await loader.LoadAsync(product);
        var router = new ProductResultRouter(_h.Store, loader, _catalog);

        (await router.RouteAsync(ProductHarness.Tenant, ProductObject)).CanSatisfyFully.ShouldBeTrue();
        _time.Advance(TimeSpan.FromMinutes(31));

        (await Should.ThrowAsync<ProductLoadException>(() => router.RouteAsync(ProductHarness.Tenant, ProductObject))).Code.ShouldBe(ProductErrorCodes.Stale);
    }

    [Fact]
    public async Task OnDemand_first_access_loads_once()
    {
        await CreateAsync(new LoadSettings(LoadMode.OnDemand, TtlMinutes: 10));
        var router = new ProductResultRouter(_h.Store, Loader(), _catalog);

        await router.RouteAsync(ProductHarness.Tenant, ProductObject);
        await router.RouteAsync(ProductHarness.Tenant, ProductObject);

        _runner.Calls.ShouldBe(1, "the first access loads, the second is served from the result");
    }

    [Fact]
    public async Task Caller_without_product_grant_is_denied()
    {
        var admin = _h.Caller(ProductHarness.Admin, admin: true);
        var product = await _h.Service.PutAsync(_h.Put(admin, "SELECT c.id FROM crm.public.customers c", ProductHarness.Bob));
        var table = ProductCatalogProjector.Project(product, null, _time.GetUtcNow());
        var pdp = new AccessDecisionPoint(
            Options.Create(new GatewayOptions()),
            new ConsentResolutionService(logger: NullLogger<ConsentResolutionService>.Instance),
            new TestInMemoryConsentRepository([], filterWindow: true),
            new TestNoOpConsentCache(),
            mandatoryFilters: new NoOpMandatoryRowFilterResolver(),
            productRepository: _h.Store);
        var subject = new SecurityPrincipalContext
        {
            IsAuthenticated = true,
            UserSid = ProductHarness.Carol,
            ResolvedTenant = ProductHarness.Tenant,
            GroupSids = new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };

        var decision = await pdp.DecideAsync(new AccessRequest(subject, table, AccessOperation.Read, AccessTransport.Rest));

        decision.IsAllowed.ShouldBeFalse("a base grant or none at all never opens a product");
    }
}
