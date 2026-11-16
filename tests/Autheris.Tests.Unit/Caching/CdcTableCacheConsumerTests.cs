namespace Autheris.Tests.Unit.Caching;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State.Caching.TableCache;
using Autheris.Application.State.Caching.TableCache.Engines;
using Autheris.Application.Streaming.Crypto;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options.TableCache;
using Autheris.Infrastructure.Caching.TableCache;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CdcTableCacheConsumerTests : IDisposable
{
    private readonly string _testDir;

    public CdcTableCacheConsumerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "autheris_cdc_consumer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Failed_apply_does_not_advance_watermark()
    {
        var streamStore = Substitute.For<ICdcStreamStore>();
        var cacheStore = Substitute.For<ITableCacheStore>();
        var catalog = new TableCacheCatalog();
        var manager = Substitute.For<ITableCacheManager>();
        var crypto = Substitute.For<ICdcStreamCrypto>();

        var table = new TableIdentifier("crm", "public", "orders");
        var streamId = CdcStreamId.New();

        catalog.Upsert(new TableCacheEntryMetadata(
            table, TableCacheStorageTier.DuckDbDisk, "/path", "v_orders", 1, DateTimeOffset.UtcNow, null, 10, 1024,
            LastWatermarkOffset: 100, IsHealthy: true, LastError: null, CachedColumns: ["id", "name"],
            FilterPredicate: null, OfflinePolicy: TableCacheOfflinePolicy.FailClosed, PrimaryKeyColumns: ["id"]));

        var plaintext = CdcPayloadCodec.EncodeChange(new CdcChangePayload(
            new Dictionary<string, object?> { ["id"] = 1 },
            null,
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "SENSITIVE_CUSTOMER_NAME" },
            BeforeUnknown: false,
            PayloadOversize: false));

        var evt = new CdcStreamEventRecord(
            streamId, 101, 1, CdcStreamEventKind.Change, CdcOperation.Insert, CdcTenantScope.Row, "t1", null,
            new CdcProtectedBlob("dek1", new byte[16]), DateTimeOffset.UtcNow);

        crypto.UnprotectEventAsync(evt, Arg.Any<CancellationToken>()).Returns(plaintext);
        streamStore.ReadAsync(streamId, 101, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new CdcReadResult(100, 101, [evt]));

        cacheStore.ApplyMutationsAsync(table, Arg.Any<IReadOnlyList<TableCacheMutation>>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Simulated mutation failure"));

        var consumer = new CdcTableCacheConsumer(streamStore, cacheStore, catalog, manager, crypto: crypto);

        await consumer.ProcessStreamBatchAsync(streamId, table);

        catalog.TryGet(table, out var updated).ShouldBeTrue();
        updated.ShouldNotBeNull();
        updated.LastWatermarkOffset.ShouldBe(100);
        updated.IsHealthy.ShouldBeFalse();
        await manager.Received(1).ForceRefreshAsync(table, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Event_without_decoded_values_fences_table()
    {
        var streamStore = Substitute.For<ICdcStreamStore>();
        var cacheStore = Substitute.For<ITableCacheStore>();
        var catalog = new TableCacheCatalog();
        var manager = Substitute.For<ITableCacheManager>();
        var crypto = Substitute.For<ICdcStreamCrypto>();

        var table = new TableIdentifier("crm", "public", "customers");
        var streamId = CdcStreamId.New();

        catalog.Upsert(new TableCacheEntryMetadata(
            table, TableCacheStorageTier.DuckDbDisk, "/path", "v_cust", 1, DateTimeOffset.UtcNow, null, 10, 1024,
            LastWatermarkOffset: 50, IsHealthy: true, LastError: null, CachedColumns: ["id", "name"],
            FilterPredicate: null, OfflinePolicy: TableCacheOfflinePolicy.FailClosed, PrimaryKeyColumns: ["id"]));

        // Decodes to empty Key / After -> carries no decoded values
        var emptyPlaintext = CdcPayloadCodec.EncodeChange(new CdcChangePayload(
            new Dictionary<string, object?>(),
            null,
            new Dictionary<string, object?>(),
            BeforeUnknown: false,
            PayloadOversize: false));

        var evt = new CdcStreamEventRecord(
            streamId, 51, 1, CdcStreamEventKind.Change, CdcOperation.Insert, CdcTenantScope.Row, "t1", null,
            new CdcProtectedBlob("dek1", new byte[16]), DateTimeOffset.UtcNow);

        crypto.UnprotectEventAsync(evt, Arg.Any<CancellationToken>()).Returns(emptyPlaintext);
        streamStore.ReadAsync(streamId, 51, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new CdcReadResult(50, 51, [evt]));

        var consumer = new CdcTableCacheConsumer(streamStore, cacheStore, catalog, manager, crypto: crypto);

        await consumer.ProcessStreamBatchAsync(streamId, table);

        await cacheStore.DidNotReceive().ApplyMutationsAsync(
            Arg.Any<TableIdentifier>(),
            Arg.Any<IReadOnlyList<TableCacheMutation>>(),
            Arg.Any<CancellationToken>());

        catalog.TryGet(table, out var updated).ShouldBeTrue();
        updated.ShouldNotBeNull();
        updated.IsHealthy.ShouldBeFalse();
        updated.LastWatermarkOffset.ShouldBe(50);
        await manager.Received(1).ForceRefreshAsync(table, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missing_pool_fences_table()
    {
        var streamStore = Substitute.For<ICdcStreamStore>();
        var catalog = new TableCacheCatalog();
        var manager = Substitute.For<ITableCacheManager>();
        var crypto = Substitute.For<ICdcStreamCrypto>();

        var opts = Options.Create(new TableCacheOptions
        {
            Enabled = true,
            DefaultStorageTier = TableCacheStorageTier.DuckDbDisk,
            PersistentDirectory = _testDir,
            Encryption = new TableCacheEncryptionOptions
            {
                Enabled = true,
                KeyVaultKeyName = "test-kek"
            }
        });
        var masterKey = Encoding.UTF8.GetBytes("01234567890123456789012345678901");
        using var realStore = new DuckDbPersistentTableCacheStore(opts, customEncryptionKey: masterKey, catalog: catalog);

        var table = new TableIdentifier("crm", "public", "missing_pool_tbl");
        var streamId = CdcStreamId.New();

        // Register metadata in catalog, but NO pool exists inside realStore (never staged/swapped or evicted)
        catalog.Upsert(new TableCacheEntryMetadata(
            table, TableCacheStorageTier.DuckDbDisk, "/missing", "v_missing", 1, DateTimeOffset.UtcNow, null, 1, 100,
            LastWatermarkOffset: 10, IsHealthy: true, LastError: null, CachedColumns: ["id", "name"],
            FilterPredicate: null, OfflinePolicy: TableCacheOfflinePolicy.FailClosed, PrimaryKeyColumns: ["id"]));

        var plaintext = CdcPayloadCodec.EncodeChange(new CdcChangePayload(
            new Dictionary<string, object?> { ["id"] = 1 },
            null,
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Alice" },
            BeforeUnknown: false,
            PayloadOversize: false));

        var evt = new CdcStreamEventRecord(
            streamId, 11, 1, CdcStreamEventKind.Change, CdcOperation.Insert, CdcTenantScope.Row, "t1", null,
            new CdcProtectedBlob("dek1", new byte[16]), DateTimeOffset.UtcNow);

        crypto.UnprotectEventAsync(evt, Arg.Any<CancellationToken>()).Returns(plaintext);
        streamStore.ReadAsync(streamId, 11, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new CdcReadResult(10, 11, [evt]));

        var consumer = new CdcTableCacheConsumer(streamStore, realStore, catalog, manager, crypto: crypto);

        await consumer.ProcessStreamBatchAsync(streamId, table);

        catalog.TryGet(table, out var fenced).ShouldBeTrue();
        fenced.ShouldNotBeNull();
        fenced.IsHealthy.ShouldBeFalse();
        fenced.LastWatermarkOffset.ShouldBe(10);
        await manager.Received(1).ForceRefreshAsync(table, Arg.Any<CancellationToken>());
    }
}
