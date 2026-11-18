using Autheris.Application.Products;
using Autheris.Application.Runtime.Hosting;
using Autheris.Application.State;
using Autheris.Application.State.Caching.TableCache;
using Autheris.Domain.Options.TableCache;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

[Trait("Category", "Security")]
public sealed class ProductScheduleTests
{
    private readonly ProductHarness _h = new();
    private readonly ScriptedRunner _runner = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));
    private readonly TableCacheCatalog _catalog = new();
    private readonly IDistributedClusterStateProvider _cluster = Substitute.For<IDistributedClusterStateProvider>();

    public ProductScheduleTests()
    {
        _cluster.TryAcquireLeaseAsync(default!, default, default).ReturnsForAnyArgs(_ => new ValueTask<IDistributedLease?>(Substitute.For<IDistributedLease>()));
    }

    private ProductScheduleJob Job(out ProductLoadService loader)
    {
        loader = new ProductLoadService(_h.Store, _runner, new RecordingCacheStore(), _catalog, _cluster, Options.Create(new TableCacheOptions()), _h.Audit, _time);
        return new ProductScheduleJob(_h.Store, loader, NullLogger<ProductScheduleJob>.Instance, _time);
    }

    private async Task<DataProduct> CreateAsync(string name, TimeSpan? interval, TenantId? tenant = null)
    {
        var t = tenant ?? ProductHarness.Tenant;
        var admin = new ProductCaller(ProductHarness.Admin, t, true, false, true);
        return await _h.Service.PutAsync(new ProductPutRequest(admin, "sales", name, DataProductKind.View, "SELECT id FROM crm.public.orders", ProductHarness.Bob,
            new LoadSettings(LoadMode.Schedule, Interval: interval)));
    }

    private static ClusterJobContext Context() => new(1, Substitute.For<IDistributedLease>());

    [Fact]
    public async Task Interval_below_one_minute_or_missing_is_rejected_and_cron_is_not_supported()
    {
        foreach (var interval in new TimeSpan?[] { null, TimeSpan.FromSeconds(59), TimeSpan.Zero, TimeSpan.FromDays(8) })
        {
            var ex = await Should.ThrowAsync<ProductException>(() => CreateAsync("bad", interval));
            ex.Code.ShouldBe(ProductErrorCodes.DefinitionInvalid);
        }

        (await CreateAsync("ok", TimeSpan.FromMinutes(1))).Load.Interval.ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_tick_loads_due_views_of_every_tenant_and_not_the_others()
    {
        await CreateAsync("view_a", TimeSpan.FromMinutes(10));
        await CreateAsync("view_b", TimeSpan.FromMinutes(10), new TenantId("tenant-b"));
        var job = Job(out var loader);

        await job.ExecuteAsync(Context(), CancellationToken.None);
        _runner.Calls.ShouldBe(2, "never loaded: both tenants' views are due");

        await job.ExecuteAsync(Context(), CancellationToken.None);
        _runner.Calls.ShouldBe(2, "a fresh result is not due");

        _time.Advance(TimeSpan.FromMinutes(11));
        await job.ExecuteAsync(Context(), CancellationToken.None);
        _runner.Calls.ShouldBe(4);
        loader.LoadedAt((await _h.Store.GetProductAsync(ProductHarness.Tenant, "sales", "view_a"))!).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_failing_view_is_not_retried_within_its_interval_and_does_not_block_the_others()
    {
        await CreateAsync("a_failing", TimeSpan.FromMinutes(10));
        await CreateAsync("b_healthy", TimeSpan.FromMinutes(10));
        _runner.Failure = call => call == 1 ? new InvalidOperationException("down") : null;
        var job = Job(out _);

        await job.ExecuteAsync(Context(), CancellationToken.None);
        _runner.Calls.ShouldBe(2);

        await job.ExecuteAsync(Context(), CancellationToken.None);
        _runner.Calls.ShouldBe(2, "the failed attempt counts: no retry before the interval has passed");

        _time.Advance(TimeSpan.FromMinutes(10));
        await job.ExecuteAsync(Context(), CancellationToken.None);
        _runner.Calls.ShouldBe(4);
    }

    [Fact]
    public async Task Live_on_demand_and_retired_products_are_never_scheduled()
    {
        var admin = _h.Caller(ProductHarness.Admin, admin: true);
        await _h.Service.PutAsync(new ProductPutRequest(admin, "sales", "live_view", DataProductKind.View, "SELECT id FROM crm.public.orders", ProductHarness.Bob, LoadSettings.Default));
        await _h.Service.PutAsync(new ProductPutRequest(admin, "sales", "od_view", DataProductKind.View, "SELECT id FROM crm.public.orders", ProductHarness.Bob, new LoadSettings(LoadMode.OnDemand, 5)));
        var retired = await CreateAsync("old_view", TimeSpan.FromMinutes(5));
        await _h.Service.RetireAsync(_h.Caller(ProductHarness.Bob), "sales", retired.Name);
        var job = Job(out _);

        await job.ExecuteAsync(Context(), CancellationToken.None);

        _runner.Calls.ShouldBe(0);
    }
}
