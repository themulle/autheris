using System.Runtime.CompilerServices;
using Autheris.Application.Products;
using Autheris.Application.State;
using Autheris.Application.State.Caching.TableCache;
using Autheris.Domain.Audit;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Options.TableCache;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

/// <summary>Cache store that consumes the rows like the real one (so limits fire) and records every generation.</summary>
internal sealed class RecordingCacheStore : ITableCacheStore
{
    public List<(TableIdentifier Key, long Epoch, int Rows)> Staged { get; } = [];

    public List<(TableIdentifier Key, long Epoch)> Dropped { get; } = [];

    public List<TableIdentifier> Evicted { get; } = [];

    public TableCacheStorageTier StorageTier => TableCacheStorageTier.Memory;

    public Task InitializeTableAsync(TableCacheTargetOptions options, CancellationToken ct = default) => Task.CompletedTask;

    public async Task<string> StageShadowGenerationAsync(TableIdentifier table, long epoch, IReadOnlyList<string> columns, IAsyncEnumerable<IReadOnlyList<object?>> rows, CancellationToken ct = default)
    {
        var count = 0;
        await foreach (var _ in rows.WithCancellation(ct))
        {
            count++;
        }

        Staged.Add((table, epoch, count));
        return $"shadow-{epoch}";
    }

    public Task<TableCacheEntryMetadata> SwapActiveGenerationAsync(TableIdentifier table, long epoch, string shadowIdentifier, long rowCount, long sizeInBytes, DateTimeOffset? expiresAt, CancellationToken ct = default) =>
        Task.FromResult(new TableCacheEntryMetadata(table, TableCacheStorageTier.Memory, "mem", shadowIdentifier, epoch, DateTimeOffset.UnixEpoch, expiresAt, rowCount, sizeInBytes,
            null, true, null, [], null, TableCacheOfflinePolicy.FailClosed));

    public Task DropGenerationAsync(TableIdentifier table, long epoch, CancellationToken ct = default)
    {
        Dropped.Add((table, epoch));
        return Task.CompletedTask;
    }

    public Task ApplyMutationsAsync(TableIdentifier table, IReadOnlyList<TableCacheMutation> mutations, CancellationToken ct = default) => Task.CompletedTask;

    public Task<System.Data.Common.DbConnection> LeaseReaderConnectionAsync(TableIdentifier table, CancellationToken ct = default) => throw new NotSupportedException();

    public void ReturnReaderConnection(TableIdentifier table, System.Data.Common.DbConnection connection)
    {
    }

    public Task EvictAsync(TableIdentifier table, CancellationToken ct = default)
    {
        Evicted.Add(table);
        return Task.CompletedTask;
    }
}

internal sealed class ScriptedRunner : IProductQueryRunner
{
    private int _calls;

    public int Calls => _calls;

    public Func<int, IReadOnlyList<IReadOnlyList<object?>>> Rows { get; set; } = _ => [[1L, "a"], [2L, "b"]];

    public Func<int, Exception?> Failure { get; set; } = _ => null;

    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<ProductQueryResult> RunAsync(DataProduct product, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _calls);
        Started.TrySetResult();
        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(ct);
        }

        if (Failure(call) is { } failure)
        {
            throw failure;
        }

        return new ProductQueryResult(["id", "name"], Stream(Rows(call)));
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> Stream(IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        foreach (var row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }
}

[Trait("Category", "Security")]
public sealed class ViewLoadTests
{
    private readonly ProductHarness _h = new();
    private readonly RecordingCacheStore _store = new();
    private readonly TableCacheCatalog _catalog = new();
    private readonly ScriptedRunner _runner = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));
    private readonly IDistributedClusterStateProvider _cluster = Substitute.For<IDistributedClusterStateProvider>();
    private TableCacheOptions _caps = new();

    private ProductLoadService Service() => new(_h.Store, _runner, _store, _catalog, _cluster, Options.Create(_caps), _h.Audit, _time);

    public ViewLoadTests()
    {
        _cluster.TryAcquireLeaseAsync(default!, default, default).ReturnsForAnyArgs(_ => new ValueTask<IDistributedLease?>(Substitute.For<IDistributedLease>()));
    }

    private async Task<DataProduct> CreateAsync(LoadSettings? load = null, ProductLimits? limits = null)
    {
        var admin = _h.Caller(ProductHarness.Admin, admin: true);
        return await _h.Service.PutAsync(new ProductPutRequest(admin, "sales", "orders_view", DataProductKind.View, "SELECT id FROM crm.public.orders", ProductHarness.Bob, load, limits));
    }

    [Fact]
    public async Task Load_runs_only_current_hash()
    {
        var product = await CreateAsync();
        var older = product with { DefinitionHash = ProductDefinitions.Hash("SELECT 1") };

        (await Should.ThrowAsync<ProductLoadException>(() => Service().LoadAsync(older))).Code.ShouldBe(ProductErrorCodes.DefinitionChanged);
        _runner.Calls.ShouldBe(0);

        // a stored definition that does not match its recorded hash is never run either
        await _h.Store.ReplaceProductAsync(product with { Definition = "SELECT secret FROM crm.public.customers" }, product.Version, null);
        var tampered = (await _h.Store.GetProductAsync(product.Tenant, "sales", "orders_view"))!;
        (await Should.ThrowAsync<ProductLoadException>(() => Service().LoadAsync(tampered))).Code.ShouldBe(ProductErrorCodes.DefinitionChanged);
        _runner.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Load_failure_keeps_result_and_marks_stale()
    {
        var product = await CreateAsync(new LoadSettings(LoadMode.Schedule, Interval: TimeSpan.FromHours(1)));
        var first = await Service().LoadAsync(product);
        var key = first.CacheKey;
        _catalog.TryGet(key, out var before).ShouldBeTrue();

        _time.Advance(TimeSpan.FromHours(1));
        _runner.Failure = _ => new InvalidOperationException("source down");
        var current = (await _h.Store.GetProductAsync(product.Tenant, "sales", "orders_view"))!;
        var ex = await Should.ThrowAsync<ProductLoadException>(() => Service().LoadAsync(current));

        ex.Code.ShouldBe(ProductErrorCodes.LoadFailed);
        _catalog.TryGet(key, out var after).ShouldBeTrue();
        after.ShouldBe(before, "the previous result stays active");
        (await _h.Store.GetProductAsync(product.Tenant, "sales", "orders_view"))!.Status.ShouldBe(DataProductStatus.Stale);
        _h.Audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ProductLoadFailed);
    }

    [Fact]
    public async Task Max_staleness_exceeded_denies()
    {
        var product = await CreateAsync(new LoadSettings(LoadMode.Schedule, Interval: TimeSpan.FromHours(1), MaxStalenessMinutes: 30));
        var service = Service();
        await service.LoadAsync(product);

        (await service.GetResultAsync(product)).Stale.ShouldBeFalse();
        _time.Advance(TimeSpan.FromMinutes(31));
        // the catalog keeps the load time of the last successful load; the clock moved on
        var denied = await Should.ThrowAsync<ProductLoadException>(() => service.GetResultAsync(product));
        denied.Code.ShouldBe(ProductErrorCodes.Stale);
    }

    [Fact]
    public async Task Limits_exceeded_fail_load()
    {
        var product = await CreateAsync(limits: new ProductLimits(MaxRows: 1));
        var ex = await Should.ThrowAsync<ProductLoadException>(() => Service().LoadAsync(product));

        ex.Code.ShouldBe(ProductErrorCodes.LimitExceeded);
        _store.Dropped.ShouldHaveSingleItem("the shadow generation of the failed load is dropped");
        _catalog.GetAll().ShouldBeEmpty();

        ProductLoadService.EffectiveLimits(new ProductLimits(0, 0, 0), new TableCacheOptions { ProductMaxRows = 5, ProductMaxBytes = 0, ProductMaxRuntimeSeconds = 10 })
            .ShouldBe(new ProductLimits(5, 0, 10));
        ProductLoadService.EffectiveLimits(new ProductLimits(100, 7, 99), new TableCacheOptions { ProductMaxRows = 5, ProductMaxBytes = 50, ProductMaxRuntimeSeconds = 10 })
            .ShouldBe(new ProductLimits(5, 7, 10), "a product limit is lowered to the global cap, never raised");
    }

    [Fact]
    public async Task Global_caps_apply_to_a_product_without_limits()
    {
        _caps = new TableCacheOptions { ProductMaxRows = 1 };
        var product = await CreateAsync();

        (await Should.ThrowAsync<ProductLoadException>(() => Service().LoadAsync(product))).Code.ShouldBe(ProductErrorCodes.LimitExceeded);
    }

    [Fact]
    public async Task Load_lease_single_runner()
    {
        var product = await CreateAsync();
        _cluster.TryAcquireLeaseAsync(default!, default, default).ReturnsForAnyArgs(_ => new ValueTask<IDistributedLease?>((IDistributedLease?)null));

        var ex = await Should.ThrowAsync<ProductLoadException>(() => Service().LoadAsync(product));

        ex.Code.ShouldBe(ProductErrorCodes.LoadInProgress);
        _runner.Calls.ShouldBe(0, "a node that does not hold the lease never runs the definition");
    }

    [Fact]
    public async Task OnDemand_first_access_loads_and_ttl_expiry_reloads()
    {
        var product = await CreateAsync(new LoadSettings(LoadMode.OnDemand, TtlMinutes: 10));
        var service = Service();

        await service.GetResultAsync(product);
        await service.GetResultAsync(product);
        _runner.Calls.ShouldBe(1, "the second access is served from the valid result");

        _time.Advance(TimeSpan.FromMinutes(11));
        var reloaded = await service.GetResultAsync(product);
        _runner.Calls.ShouldBe(2);
        reloaded.Stale.ShouldBeFalse();
    }

    [Fact]
    public async Task OnDemand_serves_the_last_result_marked_stale_when_a_reload_fails_within_max_staleness()
    {
        var product = await CreateAsync(new LoadSettings(LoadMode.OnDemand, TtlMinutes: 10, MaxStalenessMinutes: 60));
        var service = Service();
        await service.GetResultAsync(product);
        _time.Advance(TimeSpan.FromMinutes(11));
        _runner.Failure = _ => new InvalidOperationException("down");

        (await service.GetResultAsync(product)).Stale.ShouldBeTrue();
        _time.Advance(TimeSpan.FromMinutes(60));
        (await Should.ThrowAsync<ProductLoadException>(() => service.GetResultAsync(product))).Code.ShouldBe(ProductErrorCodes.Stale);
    }

    [Fact]
    public async Task OnDemand_concurrent_callers_share_one_load()
    {
        var product = await CreateAsync(new LoadSettings(LoadMode.OnDemand, TtlMinutes: 10));
        var service = Service();
        _runner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var callers = Enumerable.Range(0, 8).Select(_ => service.GetResultAsync(product)).ToList();
        await _runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _runner.Gate.SetResult();
        var results = await Task.WhenAll(callers);

        _runner.Calls.ShouldBe(1);
        results.Select(r => r.CacheKey).Distinct().ShouldHaveSingleItem();
        _store.Staged.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OnDemand_idle_product_dropped()
    {
        var product = await CreateAsync(new LoadSettings(LoadMode.OnDemand, TtlMinutes: 10, IdleDropMinutes: 30));
        var service = Service();
        var result = await service.GetResultAsync(product);

        (await service.DropIdleAsync([product])).ShouldBe(0);
        _time.Advance(TimeSpan.FromMinutes(31));
        (await service.DropIdleAsync([product])).ShouldBe(1);

        _store.Evicted.ShouldContain(result.CacheKey);
        _catalog.TryGet(result.CacheKey, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Live_loads_per_query()
    {
        var product = await CreateAsync(new LoadSettings(LoadMode.Live));
        var service = Service();

        await service.GetResultAsync(product);
        await service.GetResultAsync(product);

        _runner.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Load_mode_change_keeps_grants()
    {
        var product = await CreateAsync(new LoadSettings(LoadMode.Schedule, Interval: TimeSpan.FromHours(1)));
        var before = ProductCatalogProjector.Project(product, null, _time.GetUtcNow());

        var changed = await _h.Service.UpdateSettingsAsync(_h.Caller(ProductHarness.Bob), "sales", "orders_view", null, new LoadSettings(LoadMode.OnDemand, 15));
        var after = ProductCatalogProjector.Project(changed, null, _time.GetUtcNow());

        after.Identifier.ShouldBe(before.Identifier, "grants are keyed by the product object, which a load mode change does not touch");
        after.Table.Id.ShouldBe(before.Table.Id);
        changed.ProductOwner.ShouldBe(product.ProductOwner);
    }

    [Fact]
    public async Task Result_shared_across_callers_policies_applied_at_read()
    {
        var product = await CreateAsync(new LoadSettings(LoadMode.OnDemand, TtlMinutes: 10));
        var service = Service();

        var a = await service.GetResultAsync(product);
        var b = await service.GetResultAsync(product);

        a.CacheKey.ShouldBe(b.CacheKey);
        _runner.Calls.ShouldBe(1, "one stage-1 result serves every caller; the caller's grants are applied by stage 2");
    }

    [Fact]
    public void The_cache_key_is_tenant_scoped()
    {
        ProductSource.CacheKey(new TenantId("tenant-a"), "sales", "v").ShouldNotBe(ProductSource.CacheKey(new TenantId("tenant-b"), "sales", "v"));
    }

    [Fact]
    public async Task A_procedure_or_retired_product_is_never_loaded()
    {
        var product = await CreateAsync();
        await _h.Service.RetireAsync(_h.Caller(ProductHarness.Bob), "sales", "orders_view");

        (await Should.ThrowAsync<ProductLoadException>(() => Service().LoadAsync(product))).Code.ShouldBe(ProductErrorCodes.Unavailable);
        _runner.Calls.ShouldBe(0);
    }
}
