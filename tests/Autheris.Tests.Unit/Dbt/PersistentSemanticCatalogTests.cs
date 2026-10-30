namespace Autheris.Tests.Unit.Dbt;

using Autheris.Tests.Common;
using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#112 T-3.3 / T-3.5 / A1-3: the persistent, tenant-explicit semantic catalog.</summary>
public sealed class PersistentSemanticCatalogTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");

    private static PersistentSemanticCatalog Create(
        FakeSemanticManifestStore store,
        TenancyShape? mode = TenancyShape.Multi,
        int cacheSeconds = 30,
        TimeProvider? time = null,
        ILogger<PersistentSemanticCatalog>? logger = null) =>
        new(store, Options.Create(new SemanticOptions { ManifestCacheSeconds = cacheSeconds }), SemanticCatalogTestKit.Mode(mode), logger, time);

    [Fact]
    public async Task Restart_NewInstanceOverSameStore_ReturnsMetricsAndVersion()
    {
        var store = new FakeSemanticManifestStore();
        await Create(store).RegisterManifestAsync(SemanticCatalogTestKit.Manifest("v7", "m1", "m2"), TenantA);

        var restarted = Create(store);

        (await restarted.ListMetricsAsync(null, TenantA)).Select(m => m.Name).ShouldBe(["m1", "m2"]);
        (await restarted.GetManifestVersionAsync(TenantA)).ShouldBe("v7");
        (await restarted.GetMetricAsync("m1", TenantA))!.Version.ShouldBe("v7");
        (await restarted.GetSemanticModelAsync("orders", TenantA)).ShouldNotBeNull();
    }

    [Fact]
    public async Task ParallelFirstAccess_ReadsTheStoreOnce()
    {
        var store = new FakeSemanticManifestStore();
        await Create(store).RegisterManifestAsync(SemanticCatalogTestKit.Manifest("v1", "m1"), TenantA);
        var before = store.GetCount;
        var catalog = Create(store);
        store.Gate = new TaskCompletionSource();

        var calls = Enumerable.Range(0, 12).Select(_ => catalog.GetMetricAsync("m1", TenantA)).ToArray();
        store.Gate.SetResult();
        var results = await Task.WhenAll(calls);

        results.ShouldAllBe(m => m != null);
        (store.GetCount - before).ShouldBe(1);
    }

    [Fact]
    public async Task CacheTtl_PicksUpNewerContentHashFromAnotherNode()
    {
        var store = new FakeSemanticManifestStore();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var nodeA = Create(store, time: time);
        var nodeB = Create(store, time: time);
        await nodeA.RegisterManifestAsync(SemanticCatalogTestKit.Manifest("v1", "m1"), TenantA);
        (await nodeB.GetManifestVersionAsync(TenantA)).ShouldBe("v1");

        await nodeA.RegisterManifestAsync(SemanticCatalogTestKit.Manifest("v2", "m1", "m2"), TenantA);
        (await nodeB.GetManifestVersionAsync(TenantA)).ShouldBe("v1"); // inside the TTL
        time.Advance(TimeSpan.FromSeconds(31));

        (await nodeB.GetManifestVersionAsync(TenantA)).ShouldBe("v2");
        (await nodeB.ListMetricsAsync(null, TenantA)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task CacheSecondsZero_ChecksTheStoreOnEveryAccess()
    {
        var store = new FakeSemanticManifestStore();
        var catalog = Create(store, cacheSeconds: 0);
        await catalog.RegisterManifestAsync(SemanticCatalogTestKit.Manifest("v1", "m1"), TenantA);
        var before = store.GetCount;

        await catalog.GetMetricAsync("m1", TenantA);
        await catalog.GetMetricAsync("m1", TenantA);

        (store.GetCount - before).ShouldBe(2);
    }

    [Fact]
    public async Task CorruptJson_IsAnEmptyCatalogWithAnErrorLog_NoException()
    {
        var store = new FakeSemanticManifestStore();
        store.Put(new StoredSemanticManifest(TenantA, "v1", new string('a', 64), SemanticManifestJson.FormatVersion, 1, 1, "{not json", "x", DateTimeOffset.UtcNow));
        var logger = new RecordingLogger<PersistentSemanticCatalog>();
        var catalog = Create(store, logger: logger);

        (await catalog.ListMetricsAsync(null, TenantA)).ShouldBeEmpty();
        (await catalog.GetMetricAsync("m1", TenantA)).ShouldBeNull();
        logger.Levels.ShouldContain(LogLevel.Error);
    }

    [Fact]
    public async Task UnknownFormatVersion_IsAnEmptyCatalog()
    {
        var store = new FakeSemanticManifestStore();
        await Create(store).RegisterManifestAsync(SemanticCatalogTestKit.Manifest("v1", "m1"), TenantA);
        var row = store.Peek(TenantA)!;
        store.Put(row with { FormatVersion = SemanticManifestJson.FormatVersion + 1 });

        (await Create(store).ListMetricsAsync(null, TenantA)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ContentHashMismatch_IsAnEmptyCatalog()
    {
        var store = new FakeSemanticManifestStore();
        await Create(store).RegisterManifestAsync(SemanticCatalogTestKit.Manifest("v1", "m1"), TenantA);
        store.Put(store.Peek(TenantA)! with { ContentHash = new string('0', 64) });

        (await Create(store).ListMetricsAsync(null, TenantA)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Replace_ReImportWithoutMetricRemovesIt_AndVersionIsPerTenant()
    {
        var store = new FakeSemanticManifestStore();
        var catalog = Create(store, cacheSeconds: 0);
        await catalog.RegisterManifestAsync(SemanticCatalogTestKit.Manifest("a1", "m1", "m2"), TenantA);
        await catalog.RegisterManifestAsync(SemanticCatalogTestKit.Manifest("b1", "m1"), TenantB);

        await catalog.RegisterManifestAsync(SemanticCatalogTestKit.Manifest("a2", "m2"), TenantA);

        (await catalog.GetMetricAsync("m1", TenantA)).ShouldBeNull();
        (await catalog.GetMetricAsync("m2", TenantA)).ShouldNotBeNull();
        (await catalog.GetManifestVersionAsync(TenantA)).ShouldBe("a2");
        (await catalog.GetManifestVersionAsync(TenantB)).ShouldBe("b1");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ManifestVersion_TenantA_Import_DoesNotChangeTenantBVersion()
    {
        var store = new FakeSemanticManifestStore();
        var catalog = Create(store, cacheSeconds: 0);
        await catalog.RegisterManifestAsync(SemanticCatalogTestKit.Manifest("b1", "m1"), TenantB);

        await catalog.RegisterManifestAsync(SemanticCatalogTestKit.Manifest("a-99", "m1"), TenantA);

        (await catalog.GetManifestVersionAsync(TenantB)).ShouldBe("b1");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SemanticStore_MultiMode_TenantB_SeesNothingOfA()
    {
        var catalog = Create(new FakeSemanticManifestStore());
        await catalog.RegisterManifestAsync(SemanticCatalogTestKit.Manifest("a1", "m1"), TenantA);

        (await catalog.GetMetricAsync("m1", TenantB)).ShouldBeNull();
        (await catalog.ListMetricsAsync(null, TenantB)).ShouldBeEmpty();
        (await catalog.ListSemanticModelsAsync(TenantB)).ShouldBeEmpty();
        (await catalog.GetSemanticModelAsync("orders", TenantB)).ShouldBeNull();
        (await catalog.GetManifestVersionAsync(TenantB)).ShouldBe("1.0");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SemanticStore_DefaultTenant_TenantsAreIsolated_DefaultTenantIncluded()
    {
        var store = new FakeSemanticManifestStore();
        var catalog = Create(store, TenancyShape.Single);

        await catalog.RegisterManifestAsync(SemanticCatalogTestKit.Manifest("v1", "m1"), Autheris.Tests.Common.TestTenants.Default);

        store.Peek(Autheris.Tests.Common.TestTenants.Default).ShouldNotBeNull();
        (await catalog.GetMetricAsync("m1", Autheris.Tests.Common.TestTenants.Default)).ShouldNotBeNull();
        (await catalog.GetMetricAsync("m1", TenantA)).ShouldBeNull();
        (await catalog.GetMetricAsync("m1", TenantB)).ShouldBeNull();
        await catalog.RegisterManifestAsync(SemanticCatalogTestKit.Manifest("v2", "m2"), TenantA);
        store.Peek(TenantA).ShouldNotBeNull();
        (await catalog.GetMetricAsync("m2", Autheris.Tests.Common.TestTenants.Default)).ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TenancyShape.Multi)]
    [Trait("Category", "Security")]
    public async Task SemanticStore_UnsetOrUnknownMode_UnsetTenantIsRejected(TenancyShape? mode)
    {
        var catalog = Create(new FakeSemanticManifestStore(), mode);

        await Should.ThrowAsync<GatewayForbiddenException>(() =>
            catalog.RegisterManifestAsync(SemanticCatalogTestKit.Manifest("v1", "m1"), default));
        (await catalog.GetMetricAsync("m1", default)).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SemanticStore_NoDefaultTenant_UnsetTenantIsRejected()
    {
        var options = Options.Create(new GatewayOptions { Tenancy = new TenancyOptions() });
        var catalog = new PersistentSemanticCatalog(new FakeSemanticManifestStore(), Options.Create(new SemanticOptions()), options);

        await Should.ThrowAsync<GatewayForbiddenException>(() =>
            catalog.RegisterManifestAsync(SemanticCatalogTestKit.Manifest("v1", "m1"), default));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SemanticStore_MultiMode_UnsetTenant_Rejected()
    {
        var store = new FakeSemanticManifestStore();
        store.Put(new StoredSemanticManifest(Autheris.Tests.Common.TestTenants.Default, "v1", new string('a', 64), 1, 0, 1, "{}", "x", DateTimeOffset.UtcNow));
        var catalog = Create(store);

        (await catalog.ListMetricsAsync(null, default)).ShouldBeEmpty();
        await Should.ThrowAsync<GatewayForbiddenException>(() =>
            catalog.RegisterManifestAsync(SemanticCatalogTestKit.Manifest("v1", "m1"), default));
        store.GetCount.ShouldBe(0); // a rejected tenant never reaches the store
    }
}
