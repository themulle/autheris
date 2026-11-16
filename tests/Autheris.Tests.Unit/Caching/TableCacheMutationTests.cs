namespace Autheris.Tests.Unit.Caching;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State.Caching.TableCache;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options.TableCache;
using Autheris.Infrastructure.Caching.TableCache;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class TableCacheMutationTests : IDisposable
{
    private readonly string _testDir;
    private readonly byte[] _masterKey = Encoding.UTF8.GetBytes("01234567890123456789012345678901");

    public TableCacheMutationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "autheris_mut_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); } catch { }
        }
    }

    private TableCacheOptions CreateOptions(TableCacheStorageTier tier) => new()
    {
        Enabled = true,
        DefaultStorageTier = tier,
        PersistentDirectory = Path.Combine(_testDir, Guid.NewGuid().ToString("N")),
        DefaultMemoryLimitMb = 128,
        DefaultDiskLimitMb = 50,
        Encryption = new TableCacheEncryptionOptions
        {
            Enabled = true,
            KeyVaultKeyName = "test-kek-32bytes-key-for-cache"
        }
    };

    private ITableCacheStore CreateStore(
        TableCacheStorageTier tier,
        ITableCacheCatalog? catalog = null,
        ITableCacheInvalidator? invalidator = null,
        ILoggerFactory? loggerFactory = null)
    {
        var opts = Options.Create(CreateOptions(tier));
        return tier == TableCacheStorageTier.Memory
            ? new InMemoryTableCacheStore(opts, loggerFactory?.CreateLogger<InMemoryTableCacheStore>(), catalog, invalidator)
            : new DuckDbPersistentTableCacheStore(opts, null, loggerFactory?.CreateLogger<DuckDbPersistentTableCacheStore>(), _masterKey, catalog, invalidator);
    }

    private static async Task<TableCacheEntryMetadata> StageTypedTableAsync(
        ITableCacheStore store,
        TableIdentifier table,
        IReadOnlyList<string>? primaryKeyColumns = null,
        ITableCacheCatalog? catalog = null)
    {
        var columns = new[] { "id", "amount", "created_at", "occurred_at", "is_active", "payload", "note" };
        await store.InitializeTableAsync(new TableCacheTargetOptions
        {
            DataSource = table.Domain,
            Table = $"{table.Schema}.{table.TableName}",
            Columns = columns,
            PrimaryKeyColumns = primaryKeyColumns ?? ["id"]
        });

        async IAsyncEnumerable<IReadOnlyList<object?>> InitialRows()
        {
            yield return new object?[]
            {
                1,
                10.50m,
                new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)),
                false,
                new byte[] { 0x01, 0x02 },
                "seed"
            };
            await Task.CompletedTask;
        }

        var shadow = await store.StageShadowGenerationAsync(table, 1L, columns, InitialRows());
        var meta = await store.SwapActiveGenerationAsync(table, 1L, shadow, 1, 512, null);
        catalog?.Upsert(meta);
        return meta;
    }

    [Theory]
    [InlineData(TableCacheStorageTier.DuckDbDisk)]
    [InlineData(TableCacheStorageTier.Memory)]
    public async Task Typed_values_round_trip_under_de_DE(TableCacheStorageTier tier)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        var deCulture = CultureInfo.GetCultureInfo("de-DE");

        try
        {
            CultureInfo.CurrentCulture = deCulture;
            CultureInfo.CurrentUICulture = deCulture;

            using var store = (IDisposable)CreateStore(tier);
            var cacheStore = (ITableCacheStore)store;
            var table = new TableIdentifier("crm", "public", "typed_orders");
            var meta = await StageTypedTableAsync(cacheStore, table);

            var expectedDecimal = 12345.67m;
            var expectedDateTime = new DateTime(2026, 11, 14, 15, 30, 45, DateTimeKind.Utc);
            var expectedOffset = new DateTimeOffset(2026, 11, 14, 15, 30, 45, TimeSpan.FromHours(1));
            var expectedBool = true;
            var expectedBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x42 };

            var insert = new TableCacheMutation(
                CdcOperation.Insert,
                new Dictionary<string, object?> { ["id"] = 2 },
                new Dictionary<string, object?>
                {
                    ["id"] = 2,
                    ["amount"] = expectedDecimal,
                    ["created_at"] = expectedDateTime,
                    ["occurred_at"] = expectedOffset,
                    ["is_active"] = expectedBool,
                    ["payload"] = expectedBytes,
                    ["note"] = "inserted"
                },
                10L,
                DateTimeOffset.UtcNow);

            var updatedDecimal = 98765.43m;
            var update = new TableCacheMutation(
                CdcOperation.Update,
                new Dictionary<string, object?> { ["id"] = 2 },
                new Dictionary<string, object?>
                {
                    ["amount"] = updatedDecimal,
                    ["is_active"] = false
                },
                11L,
                DateTimeOffset.UtcNow);

            await cacheStore.ApplyMutationsAsync(table, [insert, update]);

            var conn = await cacheStore.LeaseReaderConnectionAsync(table);
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT \"id\", \"amount\", \"created_at\", \"occurred_at\", \"is_active\", \"payload\", \"note\" FROM \"{meta.ActiveViewName}\" WHERE \"id\" = 2;";
                using var reader = await cmd.ExecuteReaderAsync();
                (await reader.ReadAsync()).ShouldBeTrue();

                reader.GetInt32(0).ShouldBe(2);
                reader.GetDecimal(1).ShouldBe(updatedDecimal);
                reader.GetDateTime(2).ShouldBe(expectedDateTime);
                reader.GetFieldValue<DateTimeOffset>(3).UtcDateTime.ShouldBe(expectedOffset.UtcDateTime);
                reader.GetBoolean(4).ShouldBeFalse();
                ReadBlobBytes(reader, 5).ShouldBe(expectedBytes);
                reader.GetString(6).ShouldBe("inserted");
            }
            finally
            {
                cacheStore.ReturnReaderConnection(table, conn);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Theory]
    [InlineData(TableCacheStorageTier.DuckDbDisk)]
    [InlineData(TableCacheStorageTier.Memory)]
    public async Task Null_key_mutation_is_rejected_and_table_invalidated(TableCacheStorageTier tier)
    {
        var catalog = new TableCacheCatalog();
        ITableCacheInvalidator? innerInvalidator = null;
        var invalidatorProxy = new DeferredInvalidator(() => innerInvalidator!);
        using var store = (IDisposable)CreateStore(tier, catalog, invalidatorProxy);
        var cacheStore = (ITableCacheStore)store;
        innerInvalidator = new TableCacheInvalidator(catalog, cacheStore);

        var table = new TableIdentifier("crm", "public", "null_pk_test");
        await StageTypedTableAsync(cacheStore, table, ["id"], catalog);

        var badUpdate = new TableCacheMutation(
            CdcOperation.Update,
            new Dictionary<string, object?> { ["id"] = null },
            new Dictionary<string, object?> { ["note"] = "tampered" },
            2L,
            DateTimeOffset.UtcNow);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            cacheStore.ApplyMutationsAsync(table, [badUpdate]));

        invalidatorProxy.Calls.ShouldBe(1);
        catalog.TryGet(table, out _).ShouldBeFalse();
        await Should.ThrowAsync<KeyNotFoundException>(() => cacheStore.LeaseReaderConnectionAsync(table));
    }

    [Theory]
    [InlineData(TableCacheStorageTier.DuckDbDisk)]
    [InlineData(TableCacheStorageTier.Memory)]
    public async Task Failed_mutation_invalidates_table(TableCacheStorageTier tier)
    {
        var catalog = new TableCacheCatalog();
        using var store = (IDisposable)CreateStore(tier, catalog);
        var cacheStore = (ITableCacheStore)store;
        var table = new TableIdentifier("crm", "public", "failed_mut_test");
        await StageTypedTableAsync(cacheStore, table, ["id"], catalog);

        // First insert succeeds in-tx; second insert duplicates PK=1 and fails the transaction.
        var validInsert = new TableCacheMutation(
            CdcOperation.Insert,
            new Dictionary<string, object?> { ["id"] = 2 },
            new Dictionary<string, object?>
            {
                ["id"] = 2,
                ["amount"] = 50.00m,
                ["created_at"] = DateTime.UtcNow,
                ["occurred_at"] = DateTimeOffset.UtcNow,
                ["is_active"] = true,
                ["payload"] = new byte[] { 0x01 },
                ["note"] = "row2"
            },
            2L,
            DateTimeOffset.UtcNow);

        var duplicatePkInsert = new TableCacheMutation(
            CdcOperation.Insert,
            new Dictionary<string, object?> { ["id"] = 1 },
            new Dictionary<string, object?>
            {
                ["id"] = 1,
                ["amount"] = 99.00m,
                ["created_at"] = DateTime.UtcNow,
                ["occurred_at"] = DateTimeOffset.UtcNow,
                ["is_active"] = true,
                ["payload"] = new byte[] { 0x02 },
                ["note"] = "duplicate"
            },
            3L,
            DateTimeOffset.UtcNow);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            cacheStore.ApplyMutationsAsync(table, [validInsert, duplicatePkInsert]));

        catalog.TryGet(table, out _).ShouldBeFalse();
        await Should.ThrowAsync<KeyNotFoundException>(() => cacheStore.LeaseReaderConnectionAsync(table));
    }

    [Theory]
    [InlineData(TableCacheStorageTier.DuckDbDisk)]
    [InlineData(TableCacheStorageTier.Memory)]
    public async Task Delete_without_full_key_is_rejected(TableCacheStorageTier tier)
    {
        var catalog = new TableCacheCatalog();
        using var store = (IDisposable)CreateStore(tier, catalog);
        var cacheStore = (ITableCacheStore)store;
        var table = new TableIdentifier("crm", "public", "composite_pk_test");
        var columns = new[] { "tenant_id", "id", "note" };

        await cacheStore.InitializeTableAsync(new TableCacheTargetOptions
        {
            DataSource = table.Domain,
            Table = $"{table.Schema}.{table.TableName}",
            Columns = columns,
            PrimaryKeyColumns = ["tenant_id", "id"]
        });

        async IAsyncEnumerable<IReadOnlyList<object?>> Rows()
        {
            yield return new object?[] { "t1", 1, "keep" };
            await Task.CompletedTask;
        }

        var shadow = await cacheStore.StageShadowGenerationAsync(table, 1L, columns, Rows());
        var meta = await cacheStore.SwapActiveGenerationAsync(table, 1L, shadow, 1, 128, null);
        catalog.Upsert(meta);

        // Partial key (missing "id") -> would cause mass delete across tenant if allowed
        var partialDelete = new TableCacheMutation(
            CdcOperation.Delete,
            new Dictionary<string, object?> { ["tenant_id"] = "t1" },
            new Dictionary<string, object?>(),
            2L,
            DateTimeOffset.UtcNow);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            cacheStore.ApplyMutationsAsync(table, [partialDelete]));

        catalog.TryGet(table, out _).ShouldBeFalse();
        await Should.ThrowAsync<KeyNotFoundException>(() => cacheStore.LeaseReaderConnectionAsync(table));
    }

    [Theory]
    [InlineData(TableCacheStorageTier.DuckDbDisk)]
    [InlineData(TableCacheStorageTier.Memory)]
    public async Task Unknown_column_rejected(TableCacheStorageTier tier)
    {
        var catalog = new TableCacheCatalog();
        using var store = (IDisposable)CreateStore(tier, catalog);
        var cacheStore = (ITableCacheStore)store;
        var table = new TableIdentifier("crm", "public", "unknown_col_test");
        await StageTypedTableAsync(cacheStore, table, ["id"], catalog);

        var unknownColMutation = new TableCacheMutation(
            CdcOperation.Update,
            new Dictionary<string, object?> { ["id"] = 1 },
            new Dictionary<string, object?> { ["undeclared_col"] = "evil" },
            2L,
            DateTimeOffset.UtcNow);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            cacheStore.ApplyMutationsAsync(table, [unknownColMutation]));

        catalog.TryGet(table, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(TableCacheStorageTier.DuckDbDisk)]
    [InlineData(TableCacheStorageTier.Memory)]
    public async Task Value_with_quote_and_backslash_round_trips(TableCacheStorageTier tier)
    {
        using var store = (IDisposable)CreateStore(tier);
        var cacheStore = (ITableCacheStore)store;
        var table = new TableIdentifier("crm", "public", "escape_test");
        var meta = await StageTypedTableAsync(cacheStore, table);

        var trickyText = "O'Reilly \\ \"quoted\" '; DROP TABLE students; -- \n\\x00";
        var insert = new TableCacheMutation(
            CdcOperation.Insert,
            new Dictionary<string, object?> { ["id"] = 42 },
            new Dictionary<string, object?>
            {
                ["id"] = 42,
                ["amount"] = 1.23m,
                ["created_at"] = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc),
                ["occurred_at"] = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero),
                ["is_active"] = true,
                ["payload"] = Encoding.UTF8.GetBytes("bin\\'\""),
                ["note"] = trickyText
            },
            5L,
            DateTimeOffset.UtcNow);

        await cacheStore.ApplyMutationsAsync(table, [insert]);

        var conn = await cacheStore.LeaseReaderConnectionAsync(table);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT \"note\" FROM \"{meta.ActiveViewName}\" WHERE \"id\" = 42;";
            var note = (string?)await cmd.ExecuteScalarAsync();
            note.ShouldBe(trickyText);
        }
        finally
        {
            cacheStore.ReturnReaderConnection(table, conn);
        }
    }

    [Theory]
    [InlineData(TableCacheStorageTier.DuckDbDisk)]
    [InlineData(TableCacheStorageTier.Memory)]
    public async Task Log_contains_no_row_values(TableCacheStorageTier tier)
    {
        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider).SetMinimumLevel(LogLevel.Trace));

        var catalog = new TableCacheCatalog();
        using var store = (IDisposable)CreateStore(tier, catalog, loggerFactory: loggerFactory);
        var cacheStore = (ITableCacheStore)store;
        var table = new TableIdentifier("crm", "public", "log_scrub_test");
        await StageTypedTableAsync(cacheStore, table, ["id"], catalog);

        const string secretValue = "TOP_SECRET_ROW_VALUE_MUST_NOT_BE_LOGGED_987654";
        const string secretPkValue = "SECRET_PK_TOKEN_ABCXYZ";
        // Trigger a real DuckDB constraint failure (duplicate PK=1) while carrying sensitive row/key values
        var failingInsert = new TableCacheMutation(
            CdcOperation.Insert,
            new Dictionary<string, object?> { ["id"] = 1 },
            new Dictionary<string, object?>
            {
                ["id"] = 1,
                ["amount"] = 99.00m,
                ["created_at"] = DateTime.UtcNow,
                ["occurred_at"] = DateTimeOffset.UtcNow,
                ["is_active"] = true,
                ["payload"] = Encoding.UTF8.GetBytes(secretPkValue),
                ["note"] = secretValue
            },
            2L,
            DateTimeOffset.UtcNow);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            cacheStore.ApplyMutationsAsync(table, [failingInsert]));

        loggerProvider.Entries.ShouldNotBeEmpty();
        foreach (var entry in loggerProvider.Entries)
        {
            entry.Message.ShouldNotContain(secretValue);
            entry.Message.ShouldNotContain(secretPkValue);
            entry.Message.ShouldNotContain("INSERT INTO");
            entry.Message.ShouldNotContain("UPDATE ");
            entry.Message.ShouldNotContain("DELETE FROM");
            entry.Exception.ShouldBeNull();
        }
    }

    private static byte[] ReadBlobBytes(System.Data.Common.DbDataReader reader, int ordinal)
    {
        var val = reader.GetValue(ordinal);
        if (val is byte[] bytes)
        {
            return bytes;
        }

        if (val is Stream stream)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        throw new InvalidOperationException($"Unexpected BLOB CLR type: {val?.GetType().Name ?? "null"}");
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<CapturedLogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<CapturedLogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                entries.Enqueue(new CapturedLogEntry(logLevel, formatter(state, exception), exception));
            }
        }
    }

    private sealed record CapturedLogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class DeferredInvalidator(Func<ITableCacheInvalidator> resolve) : ITableCacheInvalidator
    {
        public int Calls { get; private set; }

        public Task InvalidateAsync(TableIdentifier table, string reason, CancellationToken ct = default)
        {
            Calls++;
            return resolve().InvalidateAsync(table, reason, ct);
        }

        public Task InvalidateDataSourceAsync(string dataSource, string reason, CancellationToken ct = default) =>
            resolve().InvalidateDataSourceAsync(dataSource, reason, ct);
    }
}
