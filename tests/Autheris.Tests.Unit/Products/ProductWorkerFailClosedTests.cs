using Autheris.Application.Audit;
using Autheris.Domain.Audit;
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

/// <summary>One failing product never starves the others, and every failure of a tick is on record.</summary>
[Trait("Category", "Security")]
public sealed class ProductWorkerFailClosedTests
{
    private readonly ProductHarness _h = new();
    private readonly ScriptedRunner _runner = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));
    private readonly TableCacheCatalog _catalog = new();
    private readonly IDistributedClusterStateProvider _cluster = Substitute.For<IDistributedClusterStateProvider>();

    private ProductLoadService Loader() =>
        new(_h.Store, _runner, new RecordingCacheStore(), _catalog, _cluster, _h.Audit, _time);

    private static ClusterJobContext Context() => new(1, new StubLease());

    private async Task<DataProduct> CreateAsync(string name)
    {
        var admin = new ProductCaller(ProductHarness.Admin, ProductHarness.Tenant, true, false, true);
        return await _h.Service.PutAsync(new ProductPutRequest(admin, "sales", name, DataProductKind.View, "SELECT id FROM crm.public.orders", ProductHarness.Bob,
            new LoadSettings(LoadMode.Schedule, Interval: TimeSpan.FromMinutes(5))));
    }

    [Fact]
    public async Task Scheduled_tick_continues_after_unexpected_exception()
    {
        await CreateAsync("a_first");
        await CreateAsync("b_second");
        _cluster.TryAcquireLeaseAsync(default!, default, default).ReturnsForAnyArgs(call =>
            call.ArgAt<string>(0).Contains("a_first", StringComparison.Ordinal)
                ? throw new InvalidOperationException("lease store down: secret-value")
                : new ValueTask<IDistributedLease?>(new StubLease()));
        var job = new ProductScheduleJob(_h.Store, Loader(), NullLogger<ProductScheduleJob>.Instance, _time);

        await job.ExecuteAsync(Context(), CancellationToken.None);

        _h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ProductLoaded && e.TargetTable == "products.sales.b_second");
        _h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ProductLoadFailed && e.TargetTable == "products.sales.a_first");
        _h.Audit.Entries.ShouldAllBe(e => !(e.DetailsJson ?? string.Empty).Contains("secret-value"));
    }

    [Fact]
    public async Task Failing_load_audits_failed_event()
    {
        var product = await CreateAsync("orders_view");
        _cluster.TryAcquireLeaseAsync(default!, default, default).ReturnsForAnyArgs(_ => new ValueTask<IDistributedLease?>(new StubLease()));
        _runner.Failure = _ => new InvalidOperationException("source down");

        await Should.ThrowAsync<ProductLoadException>(() => Loader().LoadAsync(product));

        _h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ProductLoadFailed);
    }

    [Fact]
    public async Task Idle_drop_tick_survives_store_error()
    {
        var store = Substitute.For<IOnDemandProductStore>();
        store.ListOnDemandProductsAsync(default, default, default).ReturnsForAnyArgs<Task<IReadOnlyList<DataProduct>>>(_ => throw new InvalidOperationException("store down"));
        var job = new ProductIdleDropJob(store, Loader(), NullLogger<ProductIdleDropJob>.Instance);

        await Should.NotThrowAsync(() => job.ExecuteAsync(Context(), CancellationToken.None));
    }
}
