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
using Autheris.Application.Common.Interfaces;
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
/// Conforms to 07, and 14.
/// Generation files are created and accessed via DuckDB native encryption:
/// ATTACH '<path>' AS g (ENCRYPTION_KEY '<hex(HKDF(KEK, "duckdb-gen-"+tableToken+"-"+epoch))>')
/// No plaintext generation copy or .enc file is ever written to disk.
/// </summary>
public sealed class DuckDbPersistentTableCacheStore : ITableCacheStore, IDisposable
{
    private readonly TableCacheOptions _options;
    private readonly ILogger<DuckDbPersistentTableCacheStore>? _logger;
    private readonly ITableCacheCatalog? _catalog;
    private readonly ITableCacheInvalidator? _invalidator;
    private readonly byte[]? _masterEncryptionKey;
    private readonly byte[] _metadataHmacKey;
    private readonly ConcurrentDictionary<TableIdentifier, TableCacheEntryMetadata> _metadata = new();
    private readonly ConcurrentDictionary<TableIdentifier, DuckDbConnectionPool> _connectionPools = new();
    private readonly ConcurrentDictionary<TableIdentifier, IReadOnlyList<string>> _declaredColumns = new();
    private readonly ConcurrentDictionary<TableIdentifier, IReadOnlyList<string>> _declaredPrimaryKeys = new();
    private readonly ConcurrentDictionary<(TableIdentifier Table, long Epoch), IReadOnlyList<string>> _stagedColumns = new();
    private readonly ConcurrentDictionary<TableIdentifier, string> _activeBackingTables = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public TableCacheStorageTier StorageTier => TableCacheStorageTier.DuckDbDisk;

    public DuckDbPersistentTableCacheStore(
        IOptions<TableCacheOptions> options,
        IKeyVaultSecretProvider? secretProvider = null,
        ILogger<DuckDbPersistentTableCacheStore>? logger = null,
        byte[]? customEncryptionKey = null,
        ITableCacheCatalog? catalog = null,
        ITableCacheInvalidator? invalidator = null)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger;
        _catalog = catalog;
        _invalidator = invalidator;

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
            _metadataHmacKey = RandomNumberGenerator.GetBytes(32);
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
                        cmd.CommandText = $"ATTACH '{dbFile.FullName.Replace("'", "''", StringComparison.Ordinal)}' AS test_g; DETACH test_g;";
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

    public static string SanitizeIdentifier(string identifier) =>
        TableCacheMutationHelper.SanitizeIdentifier(identifier);

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

    private string GetAttachSql(TableIdentifier table, string dbPath, long epoch)
    {
        var escapedPath = dbPath.Replace("'", "''", StringComparison.Ordinal);
        if (_options.Encryption.Enabled && _masterEncryptionKey != null)
        {
            var tableToken = GetSafeTableToken(table);
            var dek = TableCacheCryptoHelper.DeriveKey(
                _masterEncryptionKey,
                info: Encoding.UTF8.GetBytes($"duckdb-gen-{tableToken}-{epoch}"));
            var dekHex = Convert.ToHexStringLower(dek);
            return $"ATTACH '{escapedPath}' AS g (ENCRYPTION_KEY '{dekHex}'); USE g;";
        }

        return $"ATTACH '{escapedPath}' AS g; USE g;";
    }

    public async Task InitializeTableAsync(TableCacheTargetOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var table = TableCacheMutationHelper.ResolveTableIdentifier(options);
        var dir = GetTableDirectory(table);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (options.Columns is { Count: > 0 })
        {
            _declaredColumns[table] = options.Columns.Select(SanitizeIdentifier).ToArray();
        }

        if (options.PrimaryKeyColumns is { Count: > 0 })
        {
            _declaredPrimaryKeys[table] = options.PrimaryKeyColumns.Select(SanitizeIdentifier).ToArray();
        }

        if (HasPersistedEpochMetadata(dir))
        {
            await RecoverTableMetadataFromDiskAsync(table, dir, ct).ConfigureAwait(false);
        }
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
            cmd.CommandText = GetAttachSql(table, dbPath, epoch);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            cmd.CommandText = $"PRAGMA max_memory='{_options.DefaultMemoryLimitMb}MB';";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            var insertBase = $"INSERT INTO \"{shadowTable}\" ({string.Join(", ", sanitizedCols.Select(c => $"\"{c}\""))}) VALUES ";
            var batch = new List<IReadOnlyList<object?>>(1000);
            bool tableCreated = false;
            _declaredPrimaryKeys.TryGetValue(table, out var declaredPks);

            await foreach (var row in rows.WithCancellation(ct).ConfigureAwait(false))
            {
                batch.Add(row);
                if (batch.Count >= 1000)
                {
                    if (!tableCreated)
                    {
                        cmd.CommandText = TableCacheMutationHelper.BuildCreateTableSql(shadowTable, sanitizedCols, batch, declaredPks);
                        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        tableCreated = true;
                    }

                    await InsertBatchAsync(conn, insertBase, sanitizedCols.Count, batch, ct).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (!tableCreated)
            {
                cmd.CommandText = TableCacheMutationHelper.BuildCreateTableSql(shadowTable, sanitizedCols, batch, declaredPks);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (batch.Count > 0)
            {
                await InsertBatchAsync(conn, insertBase, sanitizedCols.Count, batch, ct).ConfigureAwait(false);
                batch.Clear();
            }

            cmd.CommandText = "CHECKPOINT g; USE memory; DETACH g;";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _stagedColumns[(table, epoch)] = sanitizedCols;
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
                p.Value = TableCacheMutationHelper.NormalizeParameterValue(val);
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
            // 07 Zero-Row Protection Guard:
            if (_metadata.TryGetValue(table, out var existing) && existing.RowCount > 0 && rowCount == 0)
            {
                _logger?.LogWarning("SEC-BL04-07: Aborted persistent swap for table '{Table}' because shadow staging has 0 rows while active generation has {ActiveRows} rows.", table, existing.RowCount);
                throw new InvalidOperationException($"Zero-state protection: shadow staging table '{shadow}' returned 0 rows but active generation has {existing.RowCount} rows.");
            }

            var attachSql = GetAttachSql(table, dbPath, epoch);
            using (var conn = new DuckDBConnection("DataSource=:memory:"))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = attachSql;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                cmd.CommandText = $"CREATE OR REPLACE VIEW \"{activeView}\" AS SELECT * FROM \"{shadow}\";";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                cmd.CommandText = "CHECKPOINT g; USE memory; DETACH g;";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var cachedCols = _stagedColumns.TryRemove((table, epoch), out var stagedCols)
                ? stagedCols
                : existing?.CachedColumns is { Count: > 0 } prevCols
                    ? prevCols
                    : _declaredColumns.TryGetValue(table, out var dc)
                        ? dc
                        : [];
            if (cachedCols.Count > 0)
            {
                _declaredColumns[table] = cachedCols;
            }

            var pkCols = _declaredPrimaryKeys.TryGetValue(table, out var declaredPks)
                ? declaredPks
                : existing?.PrimaryKeyColumns;

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
                CachedColumns: cachedCols,
                FilterPredicate: existing?.FilterPredicate,
                OfflinePolicy: existing?.OfflinePolicy ?? TableCacheOfflinePolicy.FailClosed,
                PrimaryKeyColumns: pkCols);

            _metadata[table] = meta;
            _activeBackingTables[table] = shadow;

            // Invalidate/replace connection pool for this table
            if (_connectionPools.TryRemove(table, out var oldPool))
            {
                oldPool.Dispose();
            }

            _connectionPools[table] = new DuckDbConnectionPool("DataSource=:memory:", attachSql);

            // Write current_epoch.meta and HMAC-SHA256 signature
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
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(mutations);
        if (mutations.Count == 0) return;

        if (!_connectionPools.TryGetValue(table, out var pool) || !_metadata.TryGetValue(table, out var meta))
        {
            var firstOp = mutations[0]?.Operation.ToString() ?? "Unknown";
            _logger?.LogError(
                "Table cache mutation batch rejected for table '{Table}' on operation {Operation} ({ErrorType}, count={MutationCount}): active connection pool missing.",
                table,
                firstOp,
                nameof(InvalidOperationException),
                mutations.Count);
            await InvalidateTableOnFailureAsync(table, "Table cache connection pool unavailable during CDC mutation.", ct).ConfigureAwait(false);
            throw new InvalidOperationException($"No active connection pool available for cached table '{table}'.");
        }

        var backingTable = _activeBackingTables.TryGetValue(table, out var bt)
            ? bt
            : GetShadowTableName(table, meta.GenerationEpoch);
        var declaredCols = meta.CachedColumns is { Count: > 0 }
            ? meta.CachedColumns
            : _declaredColumns.TryGetValue(table, out var dc)
                ? dc
                : [];
        var declaredPks = meta.PrimaryKeyColumns ?? (_declaredPrimaryKeys.TryGetValue(table, out var pks) ? pks : null);

        IReadOnlyList<TableCacheMutationHelper.PlannedMutation> planned;
        try
        {
            planned = TableCacheMutationHelper.ValidateAndPlanBatch(backingTable, mutations, declaredCols, declaredPks);
        }
        catch (Exception ex)
        {
            var firstOp = mutations[0]?.Operation.ToString() ?? "Unknown";
            _logger?.LogError(
                "Table cache mutation batch rejected for table '{Table}' on operation {Operation} ({ErrorType}, count={MutationCount}). Invalidating table.",
                table,
                firstOp,
                ex.GetType().Name,
                mutations.Count);
            await InvalidateTableOnFailureAsync(table, "Table cache mutation validation failed.", ct).ConfigureAwait(false);
            throw new InvalidOperationException("Table cache mutation validation failed; table invalidated.", ex);
        }

        bool failed = false;
        string errorType = string.Empty;
        CdcOperation failedOp = planned[0].Operation;

        using (await pool.AcquireWriterLockAsync(ct).ConfigureAwait(false))
        {
            var conn = await pool.LeaseReaderAsync(ct).ConfigureAwait(false);
            try
            {
                using var txCmd = conn.CreateCommand();
                txCmd.CommandText = "BEGIN TRANSACTION;";
                await txCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                try
                {
                    foreach (var step in planned)
                    {
                        failedOp = step.Operation;
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = step.Sql;
                        TableCacheMutationHelper.BindParameters(cmd, step.Parameters);
                        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }

                    txCmd.CommandText = "COMMIT;";
                    await txCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failed = true;
                    errorType = ex.GetType().Name;
                    try
                    {
                        txCmd.CommandText = "ROLLBACK;";
                        await txCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best-effort rollback prior to table eviction.
                    }
                }
            }
            finally
            {
                pool.ReturnReader(conn);
            }
        }

        if (failed)
        {
            _logger?.LogError(
                "Table cache mutation batch failed for table '{Table}' on operation {Operation} ({ErrorType}, count={MutationCount}). Invalidating table.",
                table,
                failedOp,
                errorType,
                mutations.Count);
            await InvalidateTableOnFailureAsync(table, "Table cache mutation execution failed.", ct).ConfigureAwait(false);
            throw new InvalidOperationException("Table cache mutation failed; table invalidated.");
        }
    }

    private async Task InvalidateTableOnFailureAsync(TableIdentifier table, string reason, CancellationToken ct)
    {
        if (_invalidator is not null)
        {
            await _invalidator.InvalidateAsync(table, reason, ct).ConfigureAwait(false);
            return;
        }

        _catalog?.Remove(table);
        await EvictAsync(table, ct).ConfigureAwait(false);
    }

    public async Task<DbConnection> LeaseReaderConnectionAsync(TableIdentifier table, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (_connectionPools.TryGetValue(table, out var pool))
        {
            return await pool.LeaseReaderAsync(ct).ConfigureAwait(false);
        }

        var dir = GetTableDirectory(table);
        if (!_metadata.TryGetValue(table, out var meta) && HasPersistedEpochMetadata(dir))
        {
            meta = await RecoverTableMetadataFromDiskAsync(table, dir, ct).ConfigureAwait(false);
        }

        if (meta != null && File.Exists(meta.InternalStoragePath))
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_connectionPools.TryGetValue(table, out pool))
                {
                    var attachSql = GetAttachSql(table, meta.InternalStoragePath, meta.GenerationEpoch);
                    pool = new DuckDbConnectionPool("DataSource=:memory:", attachSql);
                    _connectionPools[table] = pool;
                }
            }
            finally
            {
                _lock.Release();
            }

            return await pool.LeaseReaderAsync(ct).ConfigureAwait(false);
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
            _activeBackingTables.TryRemove(table, out _);

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

    // 06 Metadata HMAC Verification
    private static bool HasPersistedEpochMetadata(string dir) =>
        File.Exists(Path.Combine(dir, "current_epoch.meta")) ||
        File.Exists(Path.Combine(dir, "current_epoch.meta.hmac"));

    private async Task<TableCacheEntryMetadata> RecoverTableMetadataFromDiskAsync(
        TableIdentifier table,
        string dir,
        CancellationToken ct)
    {
        var meta = await ReadEpochMetadataWithHmacAsync(dir, ct).ConfigureAwait(false);
        if (!meta.Table.Equals(table))
        {
            throw new SecurityException($"Persisted epoch metadata in '{dir}' does not belong to table '{table}'.");
        }

        if (File.Exists(meta.InternalStoragePath))
        {
            _metadata[table] = meta;
            _activeBackingTables[table] = GetShadowTableName(table, meta.GenerationEpoch);
            if (meta.CachedColumns is { Count: > 0 } && !_declaredColumns.ContainsKey(table))
            {
                _declaredColumns[table] = meta.CachedColumns;
            }
            if (meta.PrimaryKeyColumns is { Count: > 0 } && !_declaredPrimaryKeys.ContainsKey(table))
            {
                _declaredPrimaryKeys[table] = meta.PrimaryKeyColumns;
            }
            _catalog?.Upsert(meta);
        }

        return meta;
    }

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

        // Constant-time HMAC check; throws SecurityException on mismatch
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
