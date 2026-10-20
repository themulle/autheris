namespace Autheris.Tests.Unit.Caching;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching.TableCache;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Options.TableCache;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class TableCacheManagerTests
{
    [Fact]
    public async Task Manager_DogpilePrevention_100ConcurrentRequests_DispatchesExactlyOneUpstreamQuery()
    {
        // Arrange
        var catalog = new TableCacheCatalog();
        var store = Substitute.For<ITableCacheStore>();
        var loader = Substitute.For<ITableCacheUpstreamLoader>();
        var timeProvider = new FakeTimeProvider();

        var table = new TableIdentifier("crm", "public", "customers");

        int upstreamCallCount = 0;
        loader.LoadTableDataAsync(table, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref upstreamCallCount);
                await Task.Delay(100); // Artificial latency

                async IAsyncEnumerable<IReadOnlyList<object?>> Rows()
                {
                    yield return new object?[] { "1", "Alice" };
                    await Task.CompletedTask;
                }

                return new TableCacheLoadResult(1, 1024, ["id", "name"], Rows());
            });

        store.StageShadowGenerationAsync(table, Arg.Any<long>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<IAsyncEnumerable<IReadOnlyList<object?>>>(), Arg.Any<CancellationToken>())
            .Returns("staging_crm_public_customers_1");

        store.SwapActiveGenerationAsync(table, Arg.Any<long>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TableCacheEntryMetadata(
                Table: table,
                StorageTier: TableCacheStorageTier.DuckDbDisk,
                InternalStoragePath: "/path/crm.duckdb",
                ActiveViewName: "crm_public_customers",
                GenerationEpoch: ci.ArgAt<long>(1),
                LastRefreshedAt: timeProvider.GetUtcNow(),
                ExpiresAt: timeProvider.GetUtcNow().AddHours(1),
                RowCount: 1,
                SizeInBytes: 1024,
                LastWatermarkOffset: null,
                IsHealthy: true,
                LastError: null,
                CachedColumns: ["id", "name"],
                FilterPredicate: null,
                OfflinePolicy: TableCacheOfflinePolicy.FailClosed));

        var options = new TableCacheOptions
        {
            DefaultTtlSeconds = 3600,
            Tables = [new TableCacheTargetOptions { Table = "crm.public.customers", DataSource = "crm" }]
        };

        var manager = new TableCacheManager(
            catalog,
            store,
            Options.Create(options),
            loader,
            timeProvider);

        // Act: 100 concurrent requests for an un-cached / expired table
        var tasks = new List<Task<TableCacheEntryMetadata?>>();
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(async () => await manager.GetOrLoadAsync(table)));
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Length.ShouldBe(100);
        foreach (var r in results)
        {
            r.ShouldNotBeNull();
            r.Table.ShouldBe(table);
        }

        // Upstream loader must have been invoked EXACTLY once!
        upstreamCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Manager_SingleFlight_PropagatesUpstreamError_CleansLockWithoutDeadlock()
    {
        // Arrange (T-SEC-BL04-12)
        var catalog = new TableCacheCatalog();
        var store = Substitute.For<ITableCacheStore>();
        var loader = Substitute.For<ITableCacheUpstreamLoader>();
        var table = new TableIdentifier("crm", "public", "failing_table");

        loader.LoadTableDataAsync(table, Arg.Any<CancellationToken>())
            .Returns<Task<TableCacheLoadResult>>(_ =>
            {
                throw new TimeoutException("Upstream database connection timeout");
            });

        var options = new TableCacheOptions();
        var manager = new TableCacheManager(catalog, store, Options.Create(options), loader);

        // Act: 50 concurrent requests awaiting failing upstream load
        var tasks = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await Should.ThrowAsync<TimeoutException>(async () =>
                {
                    await manager.GetOrLoadAsync(table);
                });
            }));
        }

        await Task.WhenAll(tasks);

        // Assert: flight lock must be cleanly cleared so a subsequent call doesn't deadlock
        loader.LoadTableDataAsync(table, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                async IAsyncEnumerable<IReadOnlyList<object?>> Rows()
                {
                    yield return new object?[] { "1" };
                    await Task.CompletedTask;
                }
                return Task.FromResult(new TableCacheLoadResult(1, 100, ["id"], Rows()));
            });

        store.StageShadowGenerationAsync(table, Arg.Any<long>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<IAsyncEnumerable<IReadOnlyList<object?>>>(), Arg.Any<CancellationToken>())
            .Returns("staging_ok");

        store.SwapActiveGenerationAsync(table, Arg.Any<long>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new TableCacheEntryMetadata(
                table, TableCacheStorageTier.DuckDbDisk, "/path", "view", 1, DateTimeOffset.UtcNow, null, 1, 100, null, true, null, ["id"], null, TableCacheOfflinePolicy.FailClosed));

        var recovery = await manager.GetOrLoadAsync(table);
        recovery.ShouldNotBeNull();
        recovery.IsHealthy.ShouldBeTrue();
    }

    [Fact]
    public async Task Manager_ForceRefresh_BypassesCacheAndUpdatesEpoch()
    {
        // Arrange
        var catalog = new TableCacheCatalog();
        var store = Substitute.For<ITableCacheStore>();
        var loader = Substitute.For<ITableCacheUpstreamLoader>();
        var timeProvider = new FakeTimeProvider();
        var table = new TableIdentifier("crm", "public", "customers");

        catalog.Upsert(new TableCacheEntryMetadata(
            table, TableCacheStorageTier.DuckDbDisk, "/path", "view", 100, timeProvider.GetUtcNow(), null, 1, 100, null, true, null, ["id"], null, TableCacheOfflinePolicy.FailClosed));

        async IAsyncEnumerable<IReadOnlyList<object?>> Rows()
        {
            yield return new object?[] { "2" };
            await Task.CompletedTask;
        }

        loader.LoadTableDataAsync(table, Arg.Any<CancellationToken>())
            .Returns(new TableCacheLoadResult(2, 200, ["id"], Rows()));

        store.StageShadowGenerationAsync(table, Arg.Any<long>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<IAsyncEnumerable<IReadOnlyList<object?>>>(), Arg.Any<CancellationToken>())
            .Returns("shadow_view");

        store.SwapActiveGenerationAsync(table, Arg.Any<long>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new TableCacheEntryMetadata(
                table, TableCacheStorageTier.DuckDbDisk, "/path", "view", 200, timeProvider.GetUtcNow(), null, 2, 200, null, true, null, ["id"], null, TableCacheOfflinePolicy.FailClosed));

        var manager = new TableCacheManager(catalog, store, Options.Create(new TableCacheOptions()), loader, timeProvider);

        // Act
        await manager.ForceRefreshAsync(table);

        // Assert
        catalog.TryGet(table, out var freshMeta).ShouldBeTrue();
        freshMeta.ShouldNotBeNull();
        freshMeta.GenerationEpoch.ShouldBe(200);
        freshMeta.RowCount.ShouldBe(2);
    }
}
