namespace Autheris.Infrastructure.Caching.TableCache;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options.TableCache;
using Autheris.Infrastructure.Caching.TableCache.Storage;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// INF-05: Persistent DuckDB table cache store backed by native DuckDB encrypted disk files.
/// Conforms to SEC-BL04-05, SEC-BL04-06, SEC-BL04-07, and SEC-BL04-14.
/// Generation files are created and accessed via DuckDB native encryption:
/// ATTACH '<path>' AS g (ENCRYPTION_KEY '<hex(HKDF(KEK, "duckdb-gen-"+epoch))>')
/// No plaintext generation copy or .enc file is ever written to disk.
/// </summary>
public sealed class DuckDbPersistentTableCacheStore : ITableCacheStore, IDisposable
{
    private static readonly SearchValues<char> ValidIdentifierChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_");

    private readonly TableCacheOptions _options;
    private readonly ILogger<DuckDbPersistentTableCacheStore>? _logger;
    private readonly byte[]? _masterEncryptionKey;
    private readonly byte[] _metadataHmacKey;
    private readonly ConcurrentDictionary<TableIdentifier, TableCacheEntryMetadata> _metadata = new();
    private readonly ConcurrentDictionary<TableIdentifier, DuckDbConnectionPool> _connectionPools = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public TableCacheStorageTier StorageTier => TableCacheStorageTier.DuckDbDisk;

    public DuckDbPersistentTableCacheStore(
        IOptions<TableCacheOptions> options,
        IKeyVaultSecretProvider? secretProvider = null,
        ILogger<DuckDbPersistentTableCacheStore>? logger = null,
        byte[]? customEncryptionKey = null)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger;

        if (_options.Encryption.Enabled)
        {
            if (customEncryptionKey != null)
            {
                if (customEncryptionKey.Length < 32)
                {
                    throw new InvalidOperationException($"The table cache encryption KEK must be at least 32 bytes long (got {customEncryptionKey.Length} bytes).");
                }
                _masterEncryptionKey = customEncryptionKey;
            }
            else
            {
                if (secretProvider == null)
                {
                    throw new InvalidOperationException("IKeyVaultSecretProvider is required when table cache encryption is enabled.");
                }

                if (string.IsNullOrWhiteSpace(_options.Encryption.KeyVaultKeyName))
                {
                    throw new InvalidOperationException("Encryption.KeyVaultKeyName must not be empty when table cache encryption is enabled.");
                }

                byte[]? kekBytes = null;
                try
                {
                    kekBytes = secretProvider.GetSecretBytes(_options.Encryption.KeyVaultKeyName);
                }
                catch (Exception ex) when (ex is not InvalidOperationException)
                {
                    throw new InvalidOperationException($"Failed to retrieve table cache KEK '{_options.Encryption.KeyVaultKeyName}' from secret provider.", ex);
                }

                if (kekBytes == null || kekBytes.Length < 32)
                {
                    throw new InvalidOperationException($"The table cache encryption KEK '{_options.Encryption.KeyVaultKeyName}' must be at least 32 bytes long (got {kekBytes?.Length ?? 0} bytes).");
                }

                _masterEncryptionKey = kekBytes;
            }

            _metadataHmacKey = TableCacheCryptoHelper.DeriveKey(_masterEncryptionKey, info: Encoding.UTF8.GetBytes("tablecache-meta"));
        }
        else
        {
            _masterEncryptionKey = customEncryptionKey;
            _metadataHmacKey = Array.Empty<byte>();
        }

        if (!Directory.Exists(_options.PersistentDirectory))
        {
            Directory.CreateDirectory(_options.PersistentDirectory);
        }

        CleanupLegacyAndUnencryptedFiles();
    }

    private void CleanupLegacyAndUnencryptedFiles()
    {
        if (!Directory.Exists(_options.PersistentDirectory))
        {
            return;
        }

        try
        {
            var dirInfo = new DirectoryInfo(_options.PersistentDirectory);
            // 1. Delete all legacy *.duckdb.enc files
            foreach (var encFile in dirInfo.EnumerateFiles("*.duckdb.enc", SearchOption.AllDirectories))
            {
                try { encFile.Delete(); } catch { }
            }

            // 2. If encryption is enabled, delete any unencrypted generation file
            if (_options.Encryption.Enabled)
            {
                foreach (var dbFile in dirInfo.EnumerateFiles("generation_*.duckdb", SearchOption.AllDirectories))
                {
                    bool isUnencrypted = false;
                    try
                    {
                        using var testConn = new DuckDBConnection("DataSource=:memory:");
                        testConn.Open();
                        using var cmd = testConn.CreateCommand();
                        cmd.CommandText = $"ATTACH '{dbFile.FullName.Replace("'", "''")}' AS test_g; DETACH test_g;";
                        cmd.ExecuteNonQuery();
                        isUnencrypted = true;
                    }
                    catch
                    {
                        // Fails to open without key -> it is encrypted, keep it
                        isUnencrypted = false;
                    }

                    if (isUnencrypted)
                    {
                        try { dbFile.Delete(); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to complete startup cleanup of legacy or unencrypted table cache files.");
        }
    }

    public static string SanitizeIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        var trimmed = identifier.Trim();
        if (trimmed.Length > 128)
        {
            throw new ArgumentException($"Identifier exceeds maximum length of 128 characters: '{identifier}'", nameof(identifier));
        }

        if (trimmed.AsSpan().ContainsAnyExcept(ValidIdentifierChars))
        {
            throw new ArgumentException($"Identifier contains invalid characters: '{identifier}'", nameof(identifier));
        }

        return trimmed;
    }

    public static string GetSafeTableToken(TableIdentifier table)
    {
        var raw = $"{table.Domain}_{table.Schema}_{table.TableName}".ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return hash[..16].ToLowerInvariant();
    }

    public string GetTableDirectory(TableIdentifier table)
    {
        var token = GetSafeTableToken(table);
        return Path.Combine(_options.PersistentDirectory, "tbl_" + token);
    }

    public static string GetActiveViewName(TableIdentifier table)
    {
        return "v_" + GetSafeTableToken(table);
    }

    public static string GetShadowTableName(TableIdentifier table, long epoch)
    {
        return "st_" + GetSafeTableToken(table) + "_" + Math.Max(0, epoch);
    }

    private string GetAttachSql(string dbPath, long epoch)
    {
        var escapedPath = dbPath.Replace("'", "''");
        if (_options.Encryption.Enabled && _masterEncryptionKey != null)
        {
            var dek = TableCacheCryptoHelper.DeriveKey(_masterEncryptionKey, info: Encoding.UTF8.GetBytes("duckdb-gen-" + epoch));
            var dekHex = Convert.ToHexStringLower(dek);
            return $"ATTACH '{escapedPath}' AS g (ENCRYPTION_KEY '{dekHex}'); USE g;";
        }

        return $"ATTACH '{escapedPath}' AS g; USE g;";
    }

    public async Task InitializeTableAsync(TableCacheTargetOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var table = TableIdentifier.Parse(options.Table.Contains('.') ? options.Table : $"{options.DataSource}.default.{options.Table}");
        var dir = GetTableDirectory(table);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await Task.CompletedTask;
    }

    public async Task<string> StageShadowGenerationAsync(
        TableIdentifier table,
        long epoch,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<IReadOnlyList<object?>> rows,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        EnforceDiskQuota();

        var dir = GetTableDirectory(table);
        Directory.CreateDirectory(dir);

        var shadowTable = GetShadowTableName(table, epoch);
        var dbPath = Path.Combine(dir, $"generation_{epoch}.duckdb");
        var sanitizedCols = columns.Select(SanitizeIdentifier).ToList();

        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }

        using (var conn = new DuckDBConnection("DataSource=:memory:"))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = GetAttachSql(dbPath, epoch);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            cmd.CommandText = $"PRAGMA max_memory='{_options.DefaultMemoryLimitMb}MB';";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            var colDefs = string.Join(", ", sanitizedCols.Select(c => $"\"{c}\" VARCHAR"));
            cmd.CommandText = $"CREATE TABLE \"{shadowTable}\" ({colDefs});";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            var insertBase = $"INSERT INTO \"{shadowTable}\" ({string.Join(", ", sanitizedCols.Select(c => $"\"{c}\""))}) VALUES ";
            var batch = new List<IReadOnlyList<object?>>(1000);

            await foreach (var row in rows.WithCancellation(ct).ConfigureAwait(false))
            {
                batch.Add(row);
                if (batch.Count >= 1000)
                {
                    await InsertBatchAsync(conn, insertBase, sanitizedCols.Count, batch, ct).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await InsertBatchAsync(conn, insertBase, sanitizedCols.Count, batch, ct).ConfigureAwait(false);
                batch.Clear();
            }

            cmd.CommandText = "CHECKPOINT g; DETACH g;";
            try { await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); } catch { }
        }

        return shadowTable;
    }

    private static async Task InsertBatchAsync(DuckDBConnection conn, string insertBase, int colCount, List<IReadOnlyList<object?>> batch, CancellationToken ct)
    {
        var sb = new StringBuilder(insertBase);
        using var cmd = conn.CreateCommand();
        int paramIndex = 1;
        for (int i = 0; i < batch.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('(');
            var r = batch[i];
            for (int c = 0; c < colCount; c++)
            {
                if (c > 0) sb.Append(", ");
                var val = c < r.Count ? r[c] : null;
                sb.Append($"$p{paramIndex}");

                var p = cmd.CreateParameter();
                p.ParameterName = $"p{paramIndex}";
                p.Value = val ?? DBNull.Value;
                cmd.Parameters.Add(p);

                paramIndex++;
            }
            sb.Append(')');
        }

        cmd.CommandText = sb.ToString();
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<TableCacheEntryMetadata> SwapActiveGenerationAsync(
        TableIdentifier table,
        long epoch,
        string shadowIdentifier,
        long rowCount,
        long sizeInBytes,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        var dir = GetTableDirectory(table);
        var activeView = GetActiveViewName(table);
        var dbPath = Path.Combine(dir, $"generation_{epoch}.duckdb");
        var shadow = GetShadowTableName(table, epoch);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // SEC-BL04-07 Zero-Row Protection Guard:
            if (_metadata.TryGetValue(table, out var existing) && existing.RowCount > 0 && rowCount == 0)
            {
                _logger?.LogWarning("SEC-BL04-07: Aborted persistent swap for table '{Table}' because shadow staging has 0 rows while active generation has {ActiveRows} rows.", table, existing.RowCount);
                throw new InvalidOperationException($"Zero-state protection: shadow staging table '{shadow}' returned 0 rows but active generation has {existing.RowCount} rows.");
            }

            var attachSql = GetAttachSql(dbPath, epoch);
            using (var conn = new DuckDBConnection("DataSource=:memory:"))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = attachSql;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                cmd.CommandText = $"CREATE OR REPLACE VIEW \"{activeView}\" AS SELECT * FROM \"{shadow}\";";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                cmd.CommandText = "CHECKPOINT g; DETACH g;";
                try { await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); } catch { }
            }

            var meta = new TableCacheEntryMetadata(
                Table: table,
                StorageTier: TableCacheStorageTier.DuckDbDisk,
                InternalStoragePath: dbPath,
                ActiveViewName: activeView,
                GenerationEpoch: epoch,
                LastRefreshedAt: DateTimeOffset.UtcNow,
                ExpiresAt: expiresAt,
                RowCount: rowCount,
                SizeInBytes: sizeInBytes > 0 ? sizeInBytes : (File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0),
                LastWatermarkOffset: null,
                IsHealthy: true,
                LastError: null,
                CachedColumns: existing?.CachedColumns ?? [],
                FilterPredicate: existing?.FilterPredicate,
                OfflinePolicy: existing?.OfflinePolicy ?? TableCacheOfflinePolicy.FailClosed);

            _metadata[table] = meta;

            // Invalidate/replace connection pool for this table
            if (_connectionPools.TryRemove(table, out var oldPool))
            {
                oldPool.Dispose();
            }

            _connectionPools[table] = new DuckDbConnectionPool("DataSource=:memory:", attachSql);

            // SEC-BL04-06: Write current_epoch.meta and HMAC-SHA256 signature
            await WriteEpochMetadataWithHmacAsync(dir, epoch, meta, ct).ConfigureAwait(false);

            return meta;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DropGenerationAsync(TableIdentifier table, long epoch, CancellationToken ct = default)
    {
        var dir = GetTableDirectory(table);
        var dbPath = Path.Combine(dir, $"generation_{epoch}.duckdb");

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
            await Task.CompletedTask;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ApplyMutationsAsync(
        TableIdentifier table,
        IReadOnlyList<TableCacheMutation> mutations,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        if (mutations.Count == 0) return;

        if (!_connectionPools.TryGetValue(table, out var pool))
        {
            return;
        }

        var activeView = GetActiveViewName(table);

        using (await pool.AcquireWriterLockAsync(ct).ConfigureAwait(false))
        {
            var conn = await pool.LeaseReaderAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var mut in mutations)
                {
                    using var cmd = conn.CreateCommand();
                    switch (mut.Operation)
                    {
                        case CdcOperation.Insert:
                        case CdcOperation.Snapshot:
                        {
                            var cols = mut.RowValues.Keys.Select(SanitizeIdentifier).ToList();
                            var vals = mut.RowValues.Values.Select(v => v == null ? "NULL" : $"'{v.ToString()?.Replace("'", "''")}'");
                            cmd.CommandText = $"INSERT INTO \"{activeView}\" ({string.Join(", ", cols.Select(c => $"\"{c}\""))}) VALUES ({string.Join(", ", vals)});";
                            try
                            {
                                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "Failed applying insert mutation to persistent table '{Table}'", table);
                            }
                            break;
                        }
                        case CdcOperation.Update:
                        {
                            var setClauses = mut.RowValues.Select(kv => $"\"{SanitizeIdentifier(kv.Key)}\" = {(kv.Value == null ? "NULL" : $"'{kv.Value.ToString()?.Replace("'", "''")}'")}");
                            var pkClauses = mut.PrimaryKeyValues.Select(kv => $"\"{SanitizeIdentifier(kv.Key)}\" = {(kv.Value == null ? "NULL" : $"'{kv.Value.ToString()?.Replace("'", "''")}'")}");
                            cmd.CommandText = $"UPDATE \"{activeView}\" SET {string.Join(", ", setClauses)} WHERE {string.Join(" AND ", pkClauses)};";
                            try
                            {
                                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "Failed applying update mutation to persistent table '{Table}'", table);
                            }
                            break;
                        }
                        case CdcOperation.Delete:
                        {
                            var pkClauses = mut.PrimaryKeyValues.Select(kv => $"\"{SanitizeIdentifier(kv.Key)}\" = {(kv.Value == null ? "NULL" : $"'{kv.Value.ToString()?.Replace("'", "''")}'")}");
                            cmd.CommandText = $"DELETE FROM \"{activeView}\" WHERE {string.Join(" AND ", pkClauses)};";
                            try
                            {
                                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "Failed applying delete mutation to persistent table '{Table}'", table);
                            }
                            break;
                        }
                    }
                }
            }
            finally
            {
                pool.ReturnReader(conn);
            }
        }
    }

    public async Task<DbConnection> LeaseReaderConnectionAsync(TableIdentifier table, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (_connectionPools.TryGetValue(table, out var pool))
        {
            return await pool.LeaseReaderAsync(ct).ConfigureAwait(false);
        }

        var dir = GetTableDirectory(table);
        if (_metadata.TryGetValue(table, out var meta) && File.Exists(meta.InternalStoragePath))
        {
            var attachSql = GetAttachSql(meta.InternalStoragePath, meta.GenerationEpoch);
            var newPool = new DuckDbConnectionPool("DataSource=:memory:", attachSql);
            _connectionPools[table] = newPool;
            return await newPool.LeaseReaderAsync(ct).ConfigureAwait(false);
        }

        throw new KeyNotFoundException($"No active storage connection pool available for table '{table}'.");
    }

    public void ReturnReaderConnection(TableIdentifier table, DbConnection connection)
    {
        if (_disposed)
        {
            connection.Dispose();
            return;
        }

        if (_connectionPools.TryGetValue(table, out var pool))
        {
            pool.ReturnReader(connection);
        }
        else
        {
            connection.Dispose();
        }
    }

    public async Task EvictAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connectionPools.TryRemove(table, out var pool))
            {
                pool.Dispose();
            }
            _metadata.TryRemove(table, out _);

            var dir = GetTableDirectory(table);
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
            await Task.CompletedTask;
        }
        finally
        {
            _lock.Release();
        }
    }

    // SEC-BL04-06 Metadata HMAC Verification
    private async Task WriteEpochMetadataWithHmacAsync(string dir, long epoch, TableCacheEntryMetadata meta, CancellationToken ct)
    {
        var metaFile = Path.Combine(dir, "current_epoch.meta");
        var hmacFile = Path.Combine(dir, "current_epoch.meta.hmac");

        var json = JsonSerializer.SerializeToUtf8Bytes(meta);
        var hmac = TableCacheCryptoHelper.ComputeHmac(_metadataHmacKey, json);

        await File.WriteAllBytesAsync(metaFile, json, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(hmacFile, hmac, ct).ConfigureAwait(false);
    }

    public async Task<TableCacheEntryMetadata> ReadEpochMetadataWithHmacAsync(string dir, CancellationToken ct = default)
    {
        var metaFile = Path.Combine(dir, "current_epoch.meta");
        var hmacFile = Path.Combine(dir, "current_epoch.meta.hmac");

        if (!File.Exists(metaFile) || !File.Exists(hmacFile))
        {
            throw new FileNotFoundException($"Epoch metadata or HMAC signature file not found in {dir}");
        }

        var json = await File.ReadAllBytesAsync(metaFile, ct).ConfigureAwait(false);
        var expectedHmac = await File.ReadAllBytesAsync(hmacFile, ct).ConfigureAwait(false);

        // Constant-time HMAC check; throws SecurityException on mismatch (SEC-BL04-06)
        TableCacheCryptoHelper.VerifyHmacOrThrow(_metadataHmacKey, json, expectedHmac);

        return JsonSerializer.Deserialize<TableCacheEntryMetadata>(json)
            ?? throw new InvalidDataException("Deserialized metadata is null");
    }

    private void EnforceDiskQuota()
    {
        long limitBytes = (long)_options.DefaultDiskLimitMb * 1024 * 1024;
        long totalDiskBytes = 0;

        if (Directory.Exists(_options.PersistentDirectory))
        {
            var dirInfo = new DirectoryInfo(_options.PersistentDirectory);
            totalDiskBytes = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }

        if (totalDiskBytes > limitBytes)
        {
            _logger?.LogWarning("SEC-BL04-14: Persistent disk limit exceeded ({UsedBytes} > {LimitBytes}). Triggering eviction.", totalDiskBytes, limitBytes);
            throw new InvalidOperationException($"Persistent disk storage limit exceeded: current {totalDiskBytes} bytes > quota {limitBytes} bytes.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var pool in _connectionPools.Values)
        {
            pool.Dispose();
        }
        _connectionPools.Clear();
        _lock.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
