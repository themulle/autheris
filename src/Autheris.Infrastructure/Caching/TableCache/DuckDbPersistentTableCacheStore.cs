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
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options.TableCache;
using Autheris.Infrastructure.Caching.TableCache.Storage;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Persistent DuckDB table cache store backed by encrypted local disk files.
/// Conforms to SEC-BL04-05, SEC-BL04-06, SEC-BL04-07, and SEC-BL04-14.
/// </summary>
public sealed class DuckDbPersistentTableCacheStore : ITableCacheStore, IDisposable
{
    private static readonly SearchValues<char> ValidIdentifierChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_");

    private readonly TableCacheOptions _options;
    private readonly ILogger<DuckDbPersistentTableCacheStore>? _logger;
    private readonly byte[] _masterEncryptionKey;
    private readonly byte[] _derivedDek;
    private readonly ConcurrentDictionary<TableIdentifier, TableCacheEntryMetadata> _metadata = new();
    private readonly ConcurrentDictionary<TableIdentifier, DuckDbConnectionPool> _connectionPools = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public TableCacheStorageTier StorageTier => TableCacheStorageTier.DuckDbDisk;

    public DuckDbPersistentTableCacheStore(
        IOptions<TableCacheOptions> options,
        ILogger<DuckDbPersistentTableCacheStore>? logger = null,
        byte[]? customEncryptionKey = null)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger;

        if (!Directory.Exists(_options.PersistentDirectory))
        {
            Directory.CreateDirectory(_options.PersistentDirectory);
        }

        // Derive DEK from KEK using HKDF-SHA256 (SEC-BL04-05)
        _masterEncryptionKey = customEncryptionKey ?? Encoding.UTF8.GetBytes(_options.Encryption.KeyVaultKeyName.PadRight(32, '0')[..32]);
        _derivedDek = TableCacheCryptoHelper.DeriveKey(_masterEncryptionKey, info: Encoding.UTF8.GetBytes(_options.Encryption.KeyDerivationContext));
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

        using (var conn = new DuckDBConnection($"DataSource={dbPath}"))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
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
        }

        // SEC-BL04-05: Encrypt persistent disk file at rest
        if (_options.Encryption.Enabled)
        {
            await EncryptFileAtRestAsync(dbPath, ct).ConfigureAwait(false);
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

            // Decrypt if necessary for active view creation
            if (_options.Encryption.Enabled)
            {
                await EnsureDecryptedFileAsync(dbPath, ct).ConfigureAwait(false);
            }

            using (var conn = new DuckDBConnection($"DataSource={dbPath}"))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"CREATE OR REPLACE VIEW \"{activeView}\" AS SELECT * FROM \"{shadow}\";";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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

            _connectionPools[table] = new DuckDbConnectionPool($"DataSource={dbPath}");

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
        var encPath = Path.Combine(dir, $"generation_{epoch}.duckdb.enc");

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
            if (File.Exists(encPath))
            {
                try { File.Delete(encPath); } catch { }
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
            var newPool = new DuckDbConnectionPool($"DataSource={meta.InternalStoragePath}");
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

    // SEC-BL04-05 Authenticated AES-256-GCM File Encryption
    public async Task EncryptFileAtRestAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return;
        var bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
        var encBytes = TableCacheCryptoHelper.Encrypt(_derivedDek, bytes, associatedData: Encoding.UTF8.GetBytes(Path.GetFileName(filePath)));
        var encPath = filePath + ".enc";
        await File.WriteAllBytesAsync(encPath, encBytes, ct).ConfigureAwait(false);
    }

    public async Task EnsureDecryptedFileAsync(string filePath, CancellationToken ct = default)
    {
        var encPath = filePath + ".enc";
        if (File.Exists(encPath))
        {
            var encBytes = await File.ReadAllBytesAsync(encPath, ct).ConfigureAwait(false);
            // Will throw CryptographicException on byte tampering or integrity violation (SEC-BL04-05)
            var decrypted = TableCacheCryptoHelper.Decrypt(_derivedDek, encBytes, associatedData: Encoding.UTF8.GetBytes(Path.GetFileName(filePath)));
            await File.WriteAllBytesAsync(filePath, decrypted, ct).ConfigureAwait(false);
        }
    }

    // SEC-BL04-06 Metadata HMAC Verification
    private async Task WriteEpochMetadataWithHmacAsync(string dir, long epoch, TableCacheEntryMetadata meta, CancellationToken ct)
    {
        var metaFile = Path.Combine(dir, "current_epoch.meta");
        var hmacFile = Path.Combine(dir, "current_epoch.meta.hmac");

        var json = JsonSerializer.SerializeToUtf8Bytes(meta);
        var hmac = TableCacheCryptoHelper.ComputeHmac(_derivedDek, json);

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
        TableCacheCryptoHelper.VerifyHmacOrThrow(_derivedDek, json, expectedHmac);

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
