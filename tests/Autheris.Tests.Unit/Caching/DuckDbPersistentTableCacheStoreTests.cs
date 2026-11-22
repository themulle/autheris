namespace Autheris.Tests.Unit.Caching;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.State.Caching.TableCache;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Options.TableCache;
using Autheris.Infrastructure.Caching.TableCache;
using Autheris.Infrastructure.Caching.TableCache.Storage;
using DuckDB.NET.Data;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class DuckDbPersistentTableCacheStoreTests : IDisposable
{
    private readonly string _testDir;
    private readonly DuckDbPersistentTableCacheStore _store;
    private readonly TableCacheOptions _options;
    private readonly byte[] _masterKey = Encoding.UTF8.GetBytes("01234567890123456789012345678901");

    public DuckDbPersistentTableCacheStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "autheris_cache_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        _options = new TableCacheOptions
        {
            Enabled = true,
            PersistentDirectory = _testDir,
            DefaultMemoryLimitMb = 256,
            DefaultDiskLimitMb = 50,
            Encryption = new TableCacheEncryptionOptions
            {
                Enabled = true,
                KeyVaultKeyName = "test-dek-32bytes-long-key-string"
            }
        };

        _store = new DuckDbPersistentTableCacheStore(
            Options.Create(_options),
            customEncryptionKey: _masterKey);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Store_StageShadowGeneration_WritesRowsCorrectly()
    {
        // Arrange
        var table = new TableIdentifier("crm", "public", "customers");
        long epoch = 1000;
        var columns = new List<string> { "id", "name", "country" };

        async IAsyncEnumerable<IReadOnlyList<object?>> GenerateRows()
        {
            for (int i = 1; i <= 50; i++)
            {
                yield return new object?[] { i.ToString(), $"Customer {i}", "US" };
            }
            await Task.CompletedTask;
        }

        // Act
        var shadowName = await _store.StageShadowGenerationAsync(table, epoch, columns, GenerateRows());

        // Assert
        shadowName.ShouldNotBeNullOrWhiteSpace();
        var tableDir = _store.GetTableDirectory(table);
        var dbPath = Path.Combine(tableDir, $"generation_{epoch}.duckdb");
        File.Exists(dbPath).ShouldBeTrue();
    }

    [Fact]
    public async Task Store_SwapActiveGeneration_AtomicallySwapsView()
    {
        // Arrange
        var table = new TableIdentifier("crm", "public", "customers");
        long epoch = 2000;
        var columns = new List<string> { "id", "name" };

        async IAsyncEnumerable<IReadOnlyList<object?>> GenerateRows()
        {
            yield return new object?[] { "1", "Alice" };
            yield return new object?[] { "2", "Bob" };
            await Task.CompletedTask;
        }

        var shadow = await _store.StageShadowGenerationAsync(table, epoch, columns, GenerateRows());

        // Act
        var meta = await _store.SwapActiveGenerationAsync(table, epoch, shadow, rowCount: 2, sizeInBytes: 1024, expiresAt: null);

        // Assert
        meta.ShouldNotBeNull();
        meta.RowCount.ShouldBe(2);
        meta.ActiveViewName.ShouldBe(DuckDbPersistentTableCacheStore.GetActiveViewName(table));

        var readerConn = await _store.LeaseReaderConnectionAsync(table);
        try
        {
            using var cmd = readerConn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM \"{meta.ActiveViewName}\";";
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            count.ShouldBe(2);
        }
        finally
        {
            _store.ReturnReaderConnection(table, readerConn);
        }
    }

    [Fact]
    public void Store_EncryptedFile_MatchesAes256GcmIntegrity()
    {
        // Arrange
        var dek = TableCacheCryptoHelper.DeriveKey(_masterKey);
        var plaintext = Encoding.UTF8.GetBytes("CONFIDENTIAL_TABLE_ROW_DATA_PAYLOAD");

        // Act
        var ciphertext = TableCacheCryptoHelper.Encrypt(dek, plaintext);

        // Assert
        ciphertext.ShouldNotBe(plaintext);
        var decrypted = TableCacheCryptoHelper.Decrypt(dek, ciphertext);
        Encoding.UTF8.GetString(decrypted).ShouldBe("CONFIDENTIAL_TABLE_ROW_DATA_PAYLOAD");

        // Tamper with a single byte -> CryptographicException
        ciphertext[ciphertext.Length - 1] ^= 0xFF;
        Should.Throw<CryptographicException>(() =>
        {
            TableCacheCryptoHelper.Decrypt(dek, ciphertext);
        });
    }

    [Fact]
    public async Task Store_DropGeneration_CleansUpRetiredDiskFiles()
    {
        // Arrange
        var table = new TableIdentifier("crm", "public", "customers");
        long epoch1 = 3001;
        long epoch2 = 3002;
        var columns = new List<string> { "id" };

        async IAsyncEnumerable<IReadOnlyList<object?>> GenerateRows()
        {
            yield return new object?[] { "1" };
            await Task.CompletedTask;
        }

        await _store.StageShadowGenerationAsync(table, epoch1, columns, GenerateRows());
        await _store.StageShadowGenerationAsync(table, epoch2, columns, GenerateRows());

        var tableDir = _store.GetTableDirectory(table);
        var file1 = Path.Combine(tableDir, $"generation_{epoch1}.duckdb");
        var file2 = Path.Combine(tableDir, $"generation_{epoch2}.duckdb");

        File.Exists(file1).ShouldBeTrue();
        File.Exists(file2).ShouldBeTrue();

        // Act
        await _store.DropGenerationAsync(table, epoch1);

        // Assert
        File.Exists(file1).ShouldBeFalse();
        File.Exists(file2).ShouldBeTrue();
    }

    [Fact]
    public async Task Store_MemoryAndDiskQuotas_EnforcesBoundariesWithoutOomCrash()
    {
        // Arrange
        var smallOptions = new TableCacheOptions
        {
            PersistentDirectory = _testDir,
            DefaultDiskLimitMb = 0 // 0 MB disk quota triggers quota exception
        };
        using var smallStore = new DuckDbPersistentTableCacheStore(Options.Create(smallOptions), customEncryptionKey: _masterKey);
        var table = new TableIdentifier("crm", "public", "large_table");

        // Create a dummy file in the directory to consume > 0 bytes
        var dummyFile = Path.Combine(_testDir, "dummy.dat");
        await File.WriteAllBytesAsync(dummyFile, new byte[1024]);

        async IAsyncEnumerable<IReadOnlyList<object?>> GenerateRows()
        {
            yield return new object?[] { "1" };
            await Task.CompletedTask;
        }

        // Act & Assert (T-SEC-BL04-14)
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await smallStore.StageShadowGenerationAsync(table, 5000, ["id"], GenerateRows());
        });
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Store_Encrypted_DistinctTablesAtSameEpoch_DoNotShareDek()
    {
        var tableA = new TableIdentifier("crm", "public", "customers");
        var tableB = new TableIdentifier("crm", "public", "orders");
        const long epoch = 777;

        static async IAsyncEnumerable<IReadOnlyList<object?>> Rows()
        {
            yield return new object?[] { "1", "Secret" };
            await Task.CompletedTask;
        }

        var shadowA = await _store.StageShadowGenerationAsync(tableA, epoch, ["id", "name"], Rows());
        await _store.SwapActiveGenerationAsync(tableA, epoch, shadowA, 1, 1024, null);

        var shadowB = await _store.StageShadowGenerationAsync(tableB, epoch, ["id", "name"], Rows());
        await _store.SwapActiveGenerationAsync(tableB, epoch, shadowB, 1, 1024, null);

        var dbPathA = Path.Combine(_store.GetTableDirectory(tableA), $"generation_{epoch}.duckdb");
        var escapedPathA = dbPathA.Replace("'", "''", StringComparison.Ordinal);

        // Old unbound DEK ("duckdb-gen-<epoch>") must not open tableA's generation file.
        var unboundDekHex = Convert.ToHexStringLower(
            TableCacheCryptoHelper.DeriveKey(_masterKey, info: Encoding.UTF8.GetBytes($"duckdb-gen-{epoch}")));

        using var conn = new DuckDBConnection("DataSource=:memory:");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ATTACH '{escapedPathA}' AS probe (ENCRYPTION_KEY '{unboundDekHex}');";
        await Should.ThrowAsync<Exception>(() => cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Store_InitializeAndLease_WhenMetadataTampered_ThrowsSecurityException()
    {
        var table = new TableIdentifier("crm", "public", "tamper_check");
        static async IAsyncEnumerable<IReadOnlyList<object?>> Rows()
        {
            yield return new object?[] { "1", "Alice" };
            await Task.CompletedTask;
        }

        var shadow = await _store.StageShadowGenerationAsync(table, 42, ["id", "name"], Rows());
        await _store.SwapActiveGenerationAsync(table, 42, shadow, 1, 512, null);

        var tableDir = _store.GetTableDirectory(table);
        var metaFile = Path.Combine(tableDir, "current_epoch.meta");
        var rawJson = await File.ReadAllTextAsync(metaFile);
        await File.WriteAllTextAsync(metaFile, rawJson.Replace("\"RowCount\":1", "\"RowCount\":999", StringComparison.Ordinal));

        using var reloadedStore = new DuckDbPersistentTableCacheStore(Options.Create(_options), customEncryptionKey: _masterKey);
        await Should.ThrowAsync<SecurityException>(() => reloadedStore.InitializeTableAsync(new TableCacheTargetOptions
        {
            DataSource = table.Domain,
            Table = $"{table.Schema}.{table.TableName}"
        }));

        await Should.ThrowAsync<SecurityException>(() => reloadedStore.LeaseReaderConnectionAsync(table));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Store_InitializeAndLease_RecoverValidPersistedMetadata_Succeeds()
    {
        var table = new TableIdentifier("crm", "public", "recover_ok");
        static async IAsyncEnumerable<IReadOnlyList<object?>> Rows()
        {
            yield return new object?[] { "1", "Alice" };
            yield return new object?[] { "2", "Bob" };
            await Task.CompletedTask;
        }

        var shadow = await _store.StageShadowGenerationAsync(table, 84, ["id", "name"], Rows());
        await _store.SwapActiveGenerationAsync(table, 84, shadow, 2, 512, null);

        var catalog = new TableCacheCatalog();
        using var reloadedStore = new DuckDbPersistentTableCacheStore(
            Options.Create(_options),
            customEncryptionKey: _masterKey,
            catalog: catalog);

        await reloadedStore.InitializeTableAsync(new TableCacheTargetOptions
        {
            DataSource = table.Domain,
            Table = $"{table.Schema}.{table.TableName}"
        });

        catalog.TryGet(table, out var recovered).ShouldBeTrue();
        recovered.ShouldNotBeNull();
        recovered!.RowCount.ShouldBe(2);

        var readerConn = await reloadedStore.LeaseReaderConnectionAsync(table);
        try
        {
            using var cmd = readerConn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM \"{recovered.ActiveViewName}\";";
            Convert.ToInt64(await cmd.ExecuteScalarAsync()).ShouldBe(2);
        }
        finally
        {
            reloadedStore.ReturnReaderConnection(table, readerConn);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Store_Unencrypted_GeneratesRandomHmacKey_AndRejectsTamperedOrEmptyKeyMetadata()
    {
        var unencryptedDir = Path.Combine(_testDir, "unencrypted");
        Directory.CreateDirectory(unencryptedDir);
        var unencryptedOptions = new TableCacheOptions
        {
            Enabled = true,
            PersistentDirectory = unencryptedDir,
            DefaultMemoryLimitMb = 128,
            DefaultDiskLimitMb = 50,
            Encryption = new TableCacheEncryptionOptions { Enabled = false }
        };

        using var store = new DuckDbPersistentTableCacheStore(Options.Create(unencryptedOptions));
        var table = new TableIdentifier("crm", "public", "unenc_orders");
        static async IAsyncEnumerable<IReadOnlyList<object?>> Rows()
        {
            yield return new object?[] { "1", "Order1" };
            await Task.CompletedTask;
        }

        var shadow = await store.StageShadowGenerationAsync(table, 10, ["id", "name"], Rows());
        await store.SwapActiveGenerationAsync(table, 10, shadow, 1, 256, null);

        var tableDir = store.GetTableDirectory(table);
        var validMeta = await store.ReadEpochMetadataWithHmacAsync(tableDir);
        validMeta.RowCount.ShouldBe(1);

        // Forging HMAC with empty key must be rejected (random 32-byte key is used even when encryption is disabled).
        var metaFile = Path.Combine(tableDir, "current_epoch.meta");
        var hmacFile = Path.Combine(tableDir, "current_epoch.meta.hmac");
        var originalBytes = await File.ReadAllBytesAsync(metaFile);
        var emptyKeyHmac = TableCacheCryptoHelper.ComputeHmac(Array.Empty<byte>(), originalBytes);
        await File.WriteAllBytesAsync(hmacFile, emptyKeyHmac);

        await Should.ThrowAsync<SecurityException>(() => store.ReadEpochMetadataWithHmacAsync(tableDir));

        // Tampered metadata JSON must also throw SecurityException.
        var tamperedJson = Encoding.UTF8.GetString(originalBytes).Replace("\"RowCount\":1", "\"RowCount\":999", StringComparison.Ordinal);
        await File.WriteAllTextAsync(metaFile, tamperedJson);
        await Should.ThrowAsync<SecurityException>(() => store.ReadEpochMetadataWithHmacAsync(tableDir));
    }

    [Fact]
    public async Task Store_NonPersistentStartup_InitializeWithoutMetadata_Succeeds()
    {
        var table = new TableIdentifier("crm", "public", "fresh_startup");
        await _store.InitializeTableAsync(new TableCacheTargetOptions
        {
            DataSource = table.Domain,
            Table = $"{table.Schema}.{table.TableName}",
            Columns = ["id", "name"],
            PrimaryKeyColumns = ["id"]
        });

        var tableDir = _store.GetTableDirectory(table);
        Directory.Exists(tableDir).ShouldBeTrue();
        File.Exists(Path.Combine(tableDir, "current_epoch.meta")).ShouldBeFalse();
    }
}
