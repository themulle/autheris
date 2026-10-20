namespace Autheris.Tests.Unit.Caching;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching.TableCache;
using Autheris.Application.Caching.TableCache.Engines;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options.TableCache;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class TableCacheRefreshEnginesTests
{
    [Fact]
    public async Task CdcConsumer_IncrementalMutations_AppliesInsertUpdateDelete()
    {
        // Arrange
        var streamStore = Substitute.For<ICdcStreamStore>();
        var cacheStore = Substitute.For<ITableCacheStore>();
        var catalog = new TableCacheCatalog();
        var manager = Substitute.For<ITableCacheManager>();

        var table = new TableIdentifier("crm", "public", "customers");
        var streamId = new CdcStreamId(Guid.NewGuid().ToString("N"));

        catalog.Upsert(new TableCacheEntryMetadata(
            table, TableCacheStorageTier.DuckDbDisk, "/path", "view", 1, DateTimeOffset.UtcNow, null, 10, 1000, 100, true, null, ["id", "name"], null, TableCacheOfflinePolicy.FailClosed));

        var events = new List<CdcStreamEventRecord>
        {
            new(streamId, 101, 1, CdcStreamEventKind.Change, CdcOperation.Insert, CdcTenantScope.Row, "t1", null, new CdcProtectedBlob("dek1", new byte[16]), DateTimeOffset.UtcNow),
            new(streamId, 102, 2, CdcStreamEventKind.Change, CdcOperation.Update, CdcTenantScope.Row, "t1", null, new CdcProtectedBlob("dek1", new byte[16]), DateTimeOffset.UtcNow),
            new(streamId, 103, 3, CdcStreamEventKind.Change, CdcOperation.Delete, CdcTenantScope.Row, "t1", null, new CdcProtectedBlob("dek1", new byte[16]), DateTimeOffset.UtcNow)
        };

        streamStore.ReadAsync(streamId, 101, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new CdcReadResult(100, 103, events));

        var consumer = new CdcTableCacheConsumer(streamStore, cacheStore, catalog, manager);

        // Act
        await consumer.ProcessStreamBatchAsync(streamId, table);

        // Assert
        await cacheStore.Received(1).ApplyMutationsAsync(
            table,
            Arg.Is<IReadOnlyList<TableCacheMutation>>(m => m.Count == 3),
            Arg.Any<CancellationToken>());

        catalog.TryGet(table, out var updated).ShouldBeTrue();
        updated.ShouldNotBeNull();
        updated.LastWatermarkOffset.ShouldBe(103);
        updated.IsHealthy.ShouldBeTrue();
    }

    [Fact]
    public async Task CdcConsumer_GapDetected_TriggersAutomaticShadowReload()
    {
        // Arrange (T-SEC-BL04-08)
        var streamStore = Substitute.For<ICdcStreamStore>();
        var cacheStore = Substitute.For<ITableCacheStore>();
        var catalog = new TableCacheCatalog();
        var manager = Substitute.For<ITableCacheManager>();

        var table = new TableIdentifier("crm", "public", "customers");
        var streamId = new CdcStreamId(Guid.NewGuid().ToString("N"));

        catalog.Upsert(new TableCacheEntryMetadata(
            table, TableCacheStorageTier.DuckDbDisk, "/path", "view", 1, DateTimeOffset.UtcNow, null, 10, 1000, 100, true, null, ["id", "name"], null, TableCacheOfflinePolicy.FailClosed));

        var events = new List<CdcStreamEventRecord>
        {
            new(streamId, 101, 1, CdcStreamEventKind.Gap, null, CdcTenantScope.Row, "t1", null, new CdcProtectedBlob("dek1", new byte[16]), DateTimeOffset.UtcNow)
        };

        streamStore.ReadAsync(streamId, 101, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new CdcReadResult(100, 101, events));

        var consumer = new CdcTableCacheConsumer(streamStore, cacheStore, catalog, manager);

        // Act
        await consumer.ProcessStreamBatchAsync(streamId, table);

        // Assert
        catalog.TryGet(table, out var updated).ShouldBeTrue();
        updated.ShouldNotBeNull();
        updated.IsHealthy.ShouldBeFalse(); // Fenced!

        // ForceRefreshAsync must be triggered for shadow recovery
        await manager.Received(1).ForceRefreshAsync(table, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PeriodicRefresher_IntervalElapsed_StagesShadowAndSwapsAtomically()
    {
        // Arrange
        var catalog = new TableCacheCatalog();
        var manager = Substitute.For<ITableCacheManager>();
        var timeProvider = new FakeTimeProvider();

        var table = new TableIdentifier("crm", "public", "orders");
        var targetOptions = new TableCacheTargetOptions
        {
            Table = "crm.public.orders",
            DataSource = "crm",
            RefreshMode = TableCacheRefreshMode.PeriodicInterval,
            RefreshIntervalSeconds = 300
        };

        var options = new TableCacheOptions
        {
            Enabled = true,
            Tables = [targetOptions]
        };

        catalog.Upsert(new TableCacheEntryMetadata(
            table, TableCacheStorageTier.DuckDbDisk, "/path", "view", 1, timeProvider.GetUtcNow().AddSeconds(-301), null, 10, 1000, null, true, null, ["id"], null, TableCacheOfflinePolicy.FailClosed));

        var refresher = new PeriodicTableCacheRefresher(catalog, manager, Options.Create(options), timeProvider);

        // Act
        await refresher.ExecuteCycleAsync();

        // Assert
        await manager.Received(1).ForceRefreshAsync(table, Arg.Any<CancellationToken>());
    }
}
