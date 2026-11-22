namespace Autheris.Tests.Unit.Caching;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State.Caching.TableCache;
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
        var loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loader.LoadTableDataAsync(table, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref upstreamCallCount);
                await loadGate.Task; // upstream stays in flight until the test releases it

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
        var started = 0;
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                Interlocked.Increment(ref started);
                return await manager.GetOrLoadAsync(table);
            }));
        }

        await Autheris.Tests.Common.WaitHelper.WaitUntil(() => Volatile.Read(ref started) == 100 && Volatile.Read(ref upstreamCallCount) > 0, TimeSpan.FromSeconds(30));
        loadGate.TrySetResult();
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

    [Fact]
    public async Task Manager_SingleFlight_LeaderCancels_WaitingCallerCompletesLoad()
    {
        var catalog = new TableCacheCatalog();
        var store = new StubTableCacheStore();
        var loader = new GateUpstreamLoader();
        var table = new TableIdentifier("crm", "public", "orders");
        var manager = new TableCacheManager(catalog, store, Options.Create(new TableCacheOptions()), loader, new FakeTimeProvider());

        using var leaderCts = new CancellationTokenSource();
        using var waiterCts = new CancellationTokenSource();

        var leaderTask = manager.GetOrLoadAsync(table, leaderCts.Token).AsTask();
        await loader.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var waiterTask = manager.GetOrLoadAsync(table, waiterCts.Token).AsTask();
        await leaderCts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await leaderTask.WaitAsync(TimeSpan.FromSeconds(5)));

        loader.ReleaseSuccess();
        var waiterResult = await waiterTask.WaitAsync(TimeSpan.FromSeconds(5));
        waiterResult.ShouldNotBeNull();
        waiterResult.Table.ShouldBe(table);
        loader.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Manager_SingleFlight_AllWaitersCancel_CancelsUpstreamLoad()
    {
        var catalog = new TableCacheCatalog();
        var store = new StubTableCacheStore();
        var loader = new GateUpstreamLoader();
        var table = new TableIdentifier("crm", "public", "cancelled_orders");
        var manager = new TableCacheManager(catalog, store, Options.Create(new TableCacheOptions()), loader, new FakeTimeProvider());

        using var cts = new CancellationTokenSource();
        var loadTask = manager.GetOrLoadAsync(table, cts.Token).AsTask();
        await loader.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await loadTask.WaitAsync(TimeSpan.FromSeconds(5)));
        await loader.UpstreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Manager_ProactiveEarlyRefresh_DeduplicatesConcurrentHits_AndBacksOffAfterFailure()
    {
        var t0 = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(t0);
        var catalog = new TableCacheCatalog();
        var store = new StubTableCacheStore(clock);
        var table = new TableIdentifier("crm", "public", "invoices");
        var warningLogger = new WarningSignalLogger();

        int calls = 0;
        var firstAttemptGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttemptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new DelegateUpstreamLoader(async (t, _) =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1)
            {
                await firstAttemptGate.Task.ConfigureAwait(false);
                throw new InvalidOperationException("upstream down");
            }

            secondAttemptStarted.TrySetResult();
            return GateUpstreamLoader.EmptyResult();
        });

        catalog.Upsert(new TableCacheEntryMetadata(
            table, TableCacheStorageTier.DuckDbDisk, "/path", "view", 1,
            t0.AddSeconds(-90), t0.AddSeconds(5), 1, 64, null, true, null, ["id"], null, TableCacheOfflinePolicy.FailClosed));

        var options = new TableCacheOptions { DefaultTtlSeconds = 100 };
        var manager = new TableCacheManager(catalog, store, Options.Create(options), loader, clock, warningLogger);

        // Multiple hits while first proactive refresh is still in flight / after failure should not flood upstream.
        await manager.GetOrLoadAsync(table);
        await manager.GetOrLoadAsync(table);
        await manager.GetOrLoadAsync(table);

        firstAttemptGate.TrySetResult();
        await warningLogger.FirstWarning.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Immediate retry right after failure is within backoff window -> must not invoke upstream again.
        await manager.GetOrLoadAsync(table);
        Volatile.Read(ref calls).ShouldBe(1);

        // After backoff elapses (still before ExpiresAt if extended or before expiry check), proactive refresh runs again.
        clock.Advance(TimeSpan.FromSeconds(10));
        // Re-seed valid near-expiry window so proactive refresh triggers cleanly after backoff window.
        catalog.Upsert(new TableCacheEntryMetadata(
            table, TableCacheStorageTier.DuckDbDisk, "/path", "view", 1,
            clock.GetUtcNow().AddSeconds(-95), clock.GetUtcNow().AddSeconds(5), 1, 64, null, true, null, ["id"], null, TableCacheOfflinePolicy.FailClosed));

        await manager.GetOrLoadAsync(table);
        await secondAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Read(ref calls).ShouldBe(2);
    }

    [Fact]
    public async Task Manager_UnconfiguredTable_UsesDefaultFailClosedOfflinePolicy_NotFirstTablePolicy()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var catalog = new TableCacheCatalog();
        var store = new StubTableCacheStore(clock);
        var loader = new DelegateUpstreamLoader((_, _) => Task.FromResult(GateUpstreamLoader.EmptyResult()));

        var options = new TableCacheOptions
        {
            Tables =
            [
                new TableCacheTargetOptions
                {
                    Table = "hr.dbo.employees",
                    DataSource = "hr",
                    OfflinePolicy = TableCacheOfflinePolicy.ServeStaleWithWarning
                }
            ]
        };

        var manager = new TableCacheManager(catalog, store, Options.Create(options), loader, clock);
        var unconfiguredTable = new TableIdentifier("sales", "dbo", "orders");

        var loaded = await manager.GetOrLoadAsync(unconfiguredTable);

        loaded.ShouldNotBeNull();
        loaded.OfflinePolicy.ShouldBe(TableCacheOfflinePolicy.FailClosed);
    }

    private sealed class StubTableCacheStore(TimeProvider? clock = null) : ITableCacheStore
    {
        public TableCacheStorageTier StorageTier => TableCacheStorageTier.DuckDbDisk;
        public Task InitializeTableAsync(TableCacheTargetOptions options, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> StageShadowGenerationAsync(TableIdentifier table, long epoch, IReadOnlyList<string> columns, IAsyncEnumerable<IReadOnlyList<object?>> rows, CancellationToken ct = default)
            => Task.FromResult($"shadow_{epoch}");
        public Task<TableCacheEntryMetadata> SwapActiveGenerationAsync(TableIdentifier table, long epoch, string shadowIdentifier, long rowCount, long sizeInBytes, DateTimeOffset? expiresAt, CancellationToken ct = default)
        {
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            return Task.FromResult(new TableCacheEntryMetadata(
                table, TableCacheStorageTier.DuckDbDisk, "/tmp", "v", epoch, now, expiresAt, rowCount, sizeInBytes, null, true, null, ["id"], null, TableCacheOfflinePolicy.FailClosed));
        }
        public Task DropGenerationAsync(TableIdentifier table, long epoch, CancellationToken ct = default) => Task.CompletedTask;
        public Task ApplyMutationsAsync(TableIdentifier table, IReadOnlyList<TableCacheMutation> mutations, CancellationToken ct = default) => Task.CompletedTask;
        public Task<System.Data.Common.DbConnection> LeaseReaderConnectionAsync(TableIdentifier table, CancellationToken ct = default) => throw new NotSupportedException();
        public void ReturnReaderConnection(TableIdentifier table, System.Data.Common.DbConnection connection) { }
        public Task EvictAsync(TableIdentifier table, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class GateUpstreamLoader : ITableCacheUpstreamLoader
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource UpstreamCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount => Volatile.Read(ref _calls);

        public async Task<TableCacheLoadResult> LoadTableDataAsync(TableIdentifier table, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            LoadStarted.TrySetResult();
            try
            {
                await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
                return EmptyResult();
            }
            catch (OperationCanceledException)
            {
                UpstreamCancelled.TrySetResult();
                throw;
            }
        }

        public void ReleaseSuccess() => _gate.TrySetResult();

        public static TableCacheLoadResult EmptyResult()
        {
            async IAsyncEnumerable<IReadOnlyList<object?>> Rows()
            {
                yield return new object?[] { "1" };
                await Task.CompletedTask;
            }
            return new TableCacheLoadResult(1, 64, ["id"], Rows());
        }
    }

    private sealed class DelegateUpstreamLoader(Func<TableIdentifier, CancellationToken, Task<TableCacheLoadResult>> load) : ITableCacheUpstreamLoader
    {
        public Task<TableCacheLoadResult> LoadTableDataAsync(TableIdentifier table, CancellationToken ct = default) => load(table, ct);
    }

    private sealed class WarningSignalLogger : Microsoft.Extensions.Logging.ILogger<TableCacheManager>
    {
        public TaskCompletionSource FirstWarning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                FirstWarning.TrySetResult();
            }
        }
    }
}
