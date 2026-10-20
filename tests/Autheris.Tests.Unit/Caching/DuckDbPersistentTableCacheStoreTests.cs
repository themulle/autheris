namespace Autheris.Tests.Unit.Caching;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Options.TableCache;
using Autheris.Infrastructure.Caching.TableCache;
using Autheris.Infrastructure.Caching.TableCache.Storage;
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
}
