namespace Autheris.Tests.Integration.Caching;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Options.TableCache;
using Autheris.Infrastructure.Caching.TableCache;
using Autheris.Infrastructure.Caching.TableCache.Storage;
using DuckDB.NET.Data;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DuckDbPersistentTableCacheStoreSecurityTests : IDisposable
{
    private readonly string _testDir;
    private readonly DuckDbPersistentTableCacheStore _store;
    private readonly byte[] _masterKey = Encoding.UTF8.GetBytes("01234567890123456789012345678901");

    public DuckDbPersistentTableCacheStoreSecurityTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "autheris_cache_sec_int_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        var options = new TableCacheOptions
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

        _store = new DuckDbPersistentTableCacheStore(Options.Create(options), customEncryptionKey: _masterKey);
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

        // Old unbound DEK ("duckdb-gen-<epoch>") and tableB's DEK must both fail to open tableA's generation file.
        var unboundDekHex = Convert.ToHexStringLower(
            TableCacheCryptoHelper.DeriveKey(_masterKey, info: Encoding.UTF8.GetBytes($"duckdb-gen-{epoch}")));
        var tableBDekHex = Convert.ToHexStringLower(
            TableCacheCryptoHelper.DeriveKey(_masterKey, info: Encoding.UTF8.GetBytes($"duckdb-gen-{DuckDbPersistentTableCacheStore.GetSafeTableToken(tableB)}-{epoch}")));

        using var conn = new DuckDBConnection("DataSource=:memory:");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ATTACH '{escapedPathA}' AS probe_unbound (ENCRYPTION_KEY '{unboundDekHex}');";
        await Should.ThrowAsync<Exception>(() => cmd.ExecuteNonQueryAsync());

        cmd.CommandText = $"ATTACH '{escapedPathA}' AS probe_other_table (ENCRYPTION_KEY '{tableBDekHex}');";
        await Should.ThrowAsync<Exception>(() => cmd.ExecuteNonQueryAsync());
    }
}
