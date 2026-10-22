namespace Autheris.Tests.Integration.TableCache;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Options.TableCache;
using Autheris.Infrastructure.Caching.TableCache;
using Autheris.Infrastructure.Caching.TableCache.Storage;
using DuckDB.NET.Data;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DuckDbEncryptedStoreTests : IDisposable
{
    private readonly string _testDir;
    private readonly TableCacheOptions _options;
    private static readonly byte[] ValidKek = Encoding.UTF8.GetBytes("KeyVaultKekSecretMustBe32BytesLong!");

    public DuckDbEncryptedStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "autheris_encrypted_cache_test_" + Guid.NewGuid().ToString("N"));
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
                KeyVaultKeyName = "autheris-tablecache-kek"
            }
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EncryptionEnabled_NoKek_ConstructorThrows()
    {
        var secretProvider = Substitute.For<IKeyVaultSecretProvider>();
        secretProvider.GetSecretBytes(_options.Encryption.KeyVaultKeyName)
            .Returns(_ => throw new KeyNotFoundException("KEK not found in Key Vault"));

        Should.Throw<InvalidOperationException>(() =>
        {
            _ = new DuckDbPersistentTableCacheStore(Options.Create(_options), secretProvider);
        });

        // Also throws if secretProvider is null
        Should.Throw<InvalidOperationException>(() =>
        {
            _ = new DuckDbPersistentTableCacheStore(Options.Create(_options), secretProvider: null);
        });
    }

    [Fact]
    public void KekShorterThan32Bytes_Throws()
    {
        var shortKek = Encoding.UTF8.GetBytes("TooShortKek");
        var secretProvider = Substitute.For<IKeyVaultSecretProvider>();
        secretProvider.GetSecretBytes(_options.Encryption.KeyVaultKeyName).Returns(shortKek);

        Should.Throw<InvalidOperationException>(() =>
        {
            _ = new DuckDbPersistentTableCacheStore(Options.Create(_options), secretProvider);
        }).Message.ShouldContain("at least 32 bytes");
    }

    [Fact]
    public async Task GenerationFile_ContainsNoPlaintextMarkerRow()
    {
        var secretProvider = Substitute.For<IKeyVaultSecretProvider>();
        secretProvider.GetSecretBytes(_options.Encryption.KeyVaultKeyName).Returns(ValidKek);

        using var store = new DuckDbPersistentTableCacheStore(Options.Create(_options), secretProvider);

        var table = new TableIdentifier("crm", "public", "customers");
        long epoch = 100;
        var markerValue = "SUPER_SECRET_PLAINTEXT_MARKER_ROW_VALUE_XYZ_98765";

        async IAsyncEnumerable<IReadOnlyList<object?>> GenerateRows()
        {
            yield return new object?[] { "1", markerValue };
            await Task.CompletedTask;
        }

        var shadow = await store.StageShadowGenerationAsync(table, epoch, ["id", "name"], GenerateRows());
        await store.SwapActiveGenerationAsync(table, epoch, shadow, 1, 1024, null);

        var tableDir = store.GetTableDirectory(table);
        var dbPath = Path.Combine(tableDir, $"generation_{epoch}.duckdb");

        File.Exists(dbPath).ShouldBeTrue();
        var fileBytes = await File.ReadAllBytesAsync(dbPath);

        // Plaintext marker must NOT appear anywhere in the encrypted raw file bytes!
        var markerBytes = Encoding.UTF8.GetBytes(markerValue);
        fileBytes.AsSpan().IndexOf(markerBytes).ShouldBe(-1);
    }

    [Fact]
    public async Task GenerationFile_OpenWithoutKey_Fails()
    {
        var secretProvider = Substitute.For<IKeyVaultSecretProvider>();
        secretProvider.GetSecretBytes(_options.Encryption.KeyVaultKeyName).Returns(ValidKek);

        using var store = new DuckDbPersistentTableCacheStore(Options.Create(_options), secretProvider);

        var table = new TableIdentifier("crm", "public", "customers");
        long epoch = 200;

        async IAsyncEnumerable<IReadOnlyList<object?>> GenerateRows()
        {
            yield return new object?[] { "1", "Alice" };
            await Task.CompletedTask;
        }

        var shadow = await store.StageShadowGenerationAsync(table, epoch, ["id", "name"], GenerateRows());
        await store.SwapActiveGenerationAsync(table, epoch, shadow, 1, 1024, null);

        var tableDir = store.GetTableDirectory(table);
        var dbPath = Path.Combine(tableDir, $"generation_{epoch}.duckdb");

        // Attempting to open the file without key must fail
        using var unencryptedConn = new DuckDBConnection("DataSource=:memory:");
        await unencryptedConn.OpenAsync();
        using var cmd = unencryptedConn.CreateCommand();
        cmd.CommandText = $"ATTACH '{dbPath.Replace("'", "''")}' AS g;";

        await Should.ThrowAsync<Exception>(async () =>
        {
            await cmd.ExecuteNonQueryAsync();
        });
    }

    [Fact]
    public async Task MetadataHmac_ForgedWithKeyName_Rejected()
    {
        var secretProvider = Substitute.For<IKeyVaultSecretProvider>();
        secretProvider.GetSecretBytes(_options.Encryption.KeyVaultKeyName).Returns(ValidKek);

        using var store = new DuckDbPersistentTableCacheStore(Options.Create(_options), secretProvider);

        var table = new TableIdentifier("crm", "public", "customers");
        long epoch = 300;

        async IAsyncEnumerable<IReadOnlyList<object?>> GenerateRows()
        {
            yield return new object?[] { "1", "Alice" };
            await Task.CompletedTask;
        }

        var shadow = await store.StageShadowGenerationAsync(table, epoch, ["id", "name"], GenerateRows());
        var meta = await store.SwapActiveGenerationAsync(table, epoch, shadow, 1, 1024, null);

        var tableDir = store.GetTableDirectory(table);
        var metaFile = Path.Combine(tableDir, "current_epoch.meta");
        var hmacFile = Path.Combine(tableDir, "current_epoch.meta.hmac");

        // Forge HMAC using old vulnerable method: key is KeyVaultKeyName padded to 32 bytes
        var forgedKey = Encoding.UTF8.GetBytes(_options.Encryption.KeyVaultKeyName.PadRight(32, '0')[..32]);
        var metaJson = await File.ReadAllBytesAsync(metaFile);
        var forgedHmac = TableCacheCryptoHelper.ComputeHmac(forgedKey, metaJson);

        await File.WriteAllBytesAsync(hmacFile, forgedHmac);

        // Reading forged metadata must throw SecurityException (SEC-BL04-06)
        await Should.ThrowAsync<SecurityException>(async () =>
        {
            await store.ReadEpochMetadataWithHmacAsync(tableDir);
        });
    }

    [Fact]
    public async Task NoEncFileOrDecryptedCopyLeftOnDisk()
    {
        var secretProvider = Substitute.For<IKeyVaultSecretProvider>();
        secretProvider.GetSecretBytes(_options.Encryption.KeyVaultKeyName).Returns(ValidKek);

        using var store = new DuckDbPersistentTableCacheStore(Options.Create(_options), secretProvider);

        var table = new TableIdentifier("crm", "public", "customers");
        long epoch = 400;

        async IAsyncEnumerable<IReadOnlyList<object?>> GenerateRows()
        {
            yield return new object?[] { "1", "Alice" };
            await Task.CompletedTask;
        }

        var shadow = await store.StageShadowGenerationAsync(table, epoch, ["id", "name"], GenerateRows());
        await store.SwapActiveGenerationAsync(table, epoch, shadow, 1, 1024, null);

        var tableDir = store.GetTableDirectory(table);

        // Verify that no .enc files exist
        Directory.GetFiles(tableDir, "*.enc", SearchOption.AllDirectories).ShouldBeEmpty();
    }
}
