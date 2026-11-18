using Autheris.Application.Products;
using Autheris.Application.Runtime.Hosting;
using Autheris.Application.State;
using Autheris.Application.State.Caching.TableCache;
using Autheris.Domain.Model;
using Autheris.Domain.Options.TableCache;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

/// <summary>The catalog exposure of a product follows its load status, and the idle drop reaches the products of every tenant.</summary>
[Trait("Category", "Security")]
public sealed class ProductExposureTests
{
    private readonly ProductHarness _h = new();
    private readonly ScriptedRunner _runner = new();
    private readonly TableCacheCatalog _catalog = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));
    private readonly IDistributedClusterStateProvider _cluster = Substitute.For<IDistributedClusterStateProvider>();

    public ProductExposureTests()
    {
        _h.CatalogObjects.Now = _time.GetUtcNow;
        _cluster.TryAcquireLeaseAsync(default!, default, default).ReturnsForAnyArgs(_ => new ValueTask<IDistributedLease?>(Substitute.For<IDistributedLease>()));
    }

    private ProductLoadService Loader() => new(_h.Store, _runner, new RecordingCacheStore(), _catalog, _cluster,
        Options.Create(new TableCacheOptions()), _h.Audit, _time, null, _h.CatalogObjects);

    private async Task<DataProduct> CreateAsync(LoadSettings load, TenantId? tenant = null, string name = "orders_view")
    {
        var t = tenant ?? ProductHarness.Tenant;
        var admin = new ProductCaller(ProductHarness.Admin, t, true, false, true);
        return await _h.Service.PutAsync(new ProductPutRequest(admin, "sales", name, DataProductKind.View, "SELECT id FROM crm.public.orders", ProductHarness.Bob, load));
    }

    private bool Exposed(DataProduct product) =>
        _h.CatalogObjects.Objects[ProductSource.ObjectOf(product.Tenant, product.Schema, product.Name)].Table.IsActive;

    [Fact]
    public async Task Failed_first_load_deactivates_the_catalog_object_and_a_successful_load_exposes_it()
    {
        var product = await CreateAsync(new LoadSettings(LoadMode.Schedule, Interval: TimeSpan.FromHours(1)));
        _runner.Failure = _ => new InvalidOperationException("source down");
        await Should.ThrowAsync<ProductLoadException>(() => Loader().LoadAsync(product));

        Exposed(product).ShouldBeFalse("a product without any result is invalid and not exposed");

        _runner.Failure = _ => null;
        var current = (await _h.Store.GetProductAsync(product.Tenant, "sales", "orders_view"))!;
        await Loader().LoadAsync(current);

        Exposed(product).ShouldBeTrue();
    }

    [Fact]
    public async Task Stale_product_within_max_staleness_stays_exposed()
    {
        var product = await CreateAsync(new LoadSettings(LoadMode.Schedule, Interval: TimeSpan.FromHours(1), MaxStalenessMinutes: 120));
        var loader = Loader();
        await loader.LoadAsync(product);
        _time.Advance(TimeSpan.FromMinutes(70));
        _runner.Failure = _ => new InvalidOperationException("down");
        var current = (await _h.Store.GetProductAsync(product.Tenant, "sales", "orders_view"))!;
        await Should.ThrowAsync<ProductLoadException>(() => loader.LoadAsync(current));

        (await _h.Store.GetProductAsync(product.Tenant, "sales", "orders_view"))!.Status.ShouldBe(DataProductStatus.Stale);
        Exposed(product).ShouldBeTrue("a stale product is exposed while the staleness limit allows it");
    }

    [Fact]
    public async Task Stale_product_beyond_max_staleness_is_not_exposed_after_a_failed_reload()
    {
        var product = await CreateAsync(new LoadSettings(LoadMode.Schedule, Interval: TimeSpan.FromHours(1), MaxStalenessMinutes: 30));
        var loader = Loader();
        await loader.LoadAsync(product);
        _time.Advance(TimeSpan.FromMinutes(45));
        _runner.Failure = _ => new InvalidOperationException("down");
        var current = (await _h.Store.GetProductAsync(product.Tenant, "sales", "orders_view"))!;
        await Should.ThrowAsync<ProductLoadException>(() => loader.LoadAsync(current));

        Exposed(product).ShouldBeFalse("the last result is older than the staleness limit");
    }

    [Fact]
    public async Task Idle_drop_reaches_the_products_of_every_tenant()
    {
        var load = new LoadSettings(LoadMode.OnDemand, TtlMinutes: 10, IdleDropMinutes: 30);
        var a = await CreateAsync(load);
        var b = await CreateAsync(load, new TenantId("tenant-b"));
        var loader = Loader();
        var resultA = await loader.GetResultAsync(a);
        var resultB = await loader.GetResultAsync(b);
        var job = new ProductIdleDropJob(_h.Store, loader, NullLogger<ProductIdleDropJob>.Instance);

        _time.Advance(TimeSpan.FromMinutes(31));
        await job.ExecuteAsync(new ClusterJobContext(1, Substitute.For<IDistributedLease>()), CancellationToken.None);

        _catalog.TryGet(resultA.CacheKey, out _).ShouldBeFalse();
        _catalog.TryGet(resultB.CacheKey, out _).ShouldBeFalse();
    }
}
