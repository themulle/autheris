namespace Autheris.Tests.Unit.Caching;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Caching.TableCache;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

public sealed class TableCacheCatalogTests
{
    [Fact]
    public void Catalog_Upsert_PopulatesSnapshot_FrozenDictionary()
    {
        // Arrange
        var catalog = new TableCacheCatalog();
        var table1 = new TableIdentifier("crm", "public", "customers");
        var table2 = new TableIdentifier("erp", "dbo", "products");

        var meta1 = CreateMetadata(table1, epoch: 100);
        var meta2 = CreateMetadata(table2, epoch: 200);

        // Act
        catalog.Upsert(meta1);
        catalog.Upsert(meta2);

        // Assert
        catalog.TryGet(table1, out var retrieved1).ShouldBeTrue();
        retrieved1.ShouldNotBeNull();
        retrieved1.GenerationEpoch.ShouldBe(100);

        catalog.TryGet(table2, out var retrieved2).ShouldBeTrue();
        retrieved2.ShouldNotBeNull();
        retrieved2.GenerationEpoch.ShouldBe(200);

        var snapshot = catalog.GetSnapshot();
        snapshot.Count.ShouldBe(2);
        snapshot.ContainsKey(table1).ShouldBeTrue();
        snapshot.ContainsKey(table2).ShouldBeTrue();
    }

    [Fact]
    public void Catalog_Remove_AtomicallyUpdatesSnapshot()
    {
        // Arrange
        var catalog = new TableCacheCatalog();
        var table1 = new TableIdentifier("crm", "public", "customers");
        var table2 = new TableIdentifier("erp", "dbo", "products");
        var table3 = new TableIdentifier("billing", "public", "invoices");

        catalog.Upsert(CreateMetadata(table1));
        catalog.Upsert(CreateMetadata(table2));
        catalog.Upsert(CreateMetadata(table3));

        catalog.GetSnapshot().Count.ShouldBe(3);

        // Act
        var removed = catalog.Remove(table2);

        // Assert
        removed.ShouldBeTrue();
        catalog.TryGet(table2, out _).ShouldBeFalse();
        catalog.GetSnapshot().Count.ShouldBe(2);
        catalog.GetSnapshot().ContainsKey(table2).ShouldBeFalse();
    }

    [Fact]
    public async Task Catalog_ConcurrentReadsAndUpserts_ThreadSafeWithoutLockContention()
    {
        // Arrange
        var catalog = new TableCacheCatalog();
        var table = new TableIdentifier("crm", "public", "orders");
        catalog.Upsert(CreateMetadata(table, epoch: 1));
        var done = false;

        // Act - concurrent writers and readers
        var writeTasks = new List<Task>();
        for (int i = 0; i < 5; i++)
        {
            int writerId = i;
            writeTasks.Add(Task.Run(async () =>
            {
                long epoch = 0;
                while (!done)
                {
                    epoch++;
                    catalog.Upsert(CreateMetadata(table, epoch: epoch));
                    await Task.Yield();
                }
            }));
        }

        var readTasks = new List<Task>();
        for (int i = 0; i < 15; i++)
        {
            readTasks.Add(Task.Run(async () =>
            {
                while (!done)
                {
                    var snapshot = catalog.GetSnapshot();
                    snapshot.ShouldNotBeNull();
                    await Task.Yield();
                }
            }));
        }

        await Task.Delay(300); // scenario: fixed 300 ms stress window for concurrent readers and writers
        done = true;

        await Task.WhenAll(writeTasks);
        await Task.WhenAll(readTasks);

        // Assert
        catalog.TryGet(table, out var finalMeta).ShouldBeTrue();
        finalMeta.ShouldNotBeNull();
        finalMeta.GenerationEpoch.ShouldBeGreaterThan(0);
    }

    private static TableCacheEntryMetadata CreateMetadata(TableIdentifier table, long epoch = 1)
    {
        return new TableCacheEntryMetadata(
            Table: table,
            StorageTier: TableCacheStorageTier.DuckDbDisk,
            InternalStoragePath: $"/var/lib/cache/{table}.duckdb",
            ActiveViewName: $"{table.Domain}_{table.Schema}_{table.TableName}",
            GenerationEpoch: epoch,
            LastRefreshedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            RowCount: 100,
            SizeInBytes: 1024,
            LastWatermarkOffset: 10,
            IsHealthy: true,
            LastError: null,
            CachedColumns: ["id", "tenant_id", "name"],
            FilterPredicate: null,
            OfflinePolicy: TableCacheOfflinePolicy.FailClosed);
    }
}
