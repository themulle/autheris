namespace Autheris.Infrastructure.Caching.TableCache;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
/// Volatile in-memory DuckDB table cache store.
/// Provides sub-millisecond query execution over in-memory columnar tables.
/// Conforms to 07, and 14.
/// </summary>
public sealed class InMemoryTableCacheStore : ITableCacheStore, IDisposable
{
    private readonly TableCacheOptions _options;
    private readonly ILogger<InMemoryTableCacheStore>? _logger;
    private readonly ITableCacheCatalog? _catalog;
    private readonly ITableCacheInvalidator? _invalidator;
    private readonly DuckDBConnection _sharedConnection;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<TableIdentifier, TableCacheEntryMetadata> _metadata = new();
    private readonly ConcurrentDictionary<TableIdentifier, IReadOnlyList<string>> _declaredColumns = new();
    private readonly ConcurrentDictionary<TableIdentifier, IReadOnlyList<string>> _declaredPrimaryKeys = new();
    private readonly ConcurrentDictionary<(TableIdentifier Table, long Epoch), IReadOnlyList<string>> _stagedColumns = new();
    private readonly ConcurrentDictionary<TableIdentifier, string> _activeBackingTables = new();
    private bool _disposed;

    public TableCacheStorageTier StorageTier => TableCacheStorageTier.Memory;

    public InMemoryTableCacheStore(
        IOptions<TableCacheOptions> options,
        ILogger<InMemoryTableCacheStore>? logger = null,
        ITableCacheCatalog? catalog = null,
        ITableCacheInvalidator? invalidator = null)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger;
        _catalog = catalog;
        _invalidator = invalidator;
        _sharedConnection = new DuckDBConnection("DataSource=:memory:");
        _sharedConnection.Open();

        using var cmd = _sharedConnection.CreateCommand();
        cmd.CommandText = $"PRAGMA max_memory='{_options.DefaultMemoryLimitMb}MB'; PRAGMA threads=4;";
        cmd.ExecuteNonQuery();
    }

    public static string SanitizeIdentifier(string identifier) =>
        TableCacheMutationHelper.SanitizeIdentifier(identifier);

    public static string GetSafeTableToken(TableIdentifier table)
    {
        var raw = $"{table.Domain}_{table.Schema}_{table.TableName}".ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return hash[..16].ToLowerInvariant();
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
        var table = TableCacheMutationHelper.ResolveTableIdentifier(options);

        if (options.Columns is { Count: > 0 })
        {
            _declaredColumns[table] = options.Columns.Select(SanitizeIdentifier).ToArray();
        }

        if (options.PrimaryKeyColumns is { Count: > 0 })
        {
            _declaredPrimaryKeys[table] = options.PrimaryKeyColumns.Select(SanitizeIdentifier).ToArray();
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

        var shadowTable = GetShadowTableName(table, epoch);
        var sanitizedCols = columns.Select(SanitizeIdentifier).ToList();

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _sharedConnection.CreateCommand();
            cmd.CommandText = $"DROP TABLE IF EXISTS \"{shadowTable}\";";
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

                    await InsertBatchAsync(insertBase, sanitizedCols.Count, batch, ct).ConfigureAwait(false);
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
                await InsertBatchAsync(insertBase, sanitizedCols.Count, batch, ct).ConfigureAwait(false);
                batch.Clear();
            }

            _stagedColumns[(table, epoch)] = sanitizedCols;

            return shadowTable;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task InsertBatchAsync(string insertBase, int colCount, List<IReadOnlyList<object?>> batch, CancellationToken ct)
    {
        var sb = new StringBuilder(insertBase);
        using var cmd = _sharedConnection.CreateCommand();
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
        var shadow = GetShadowTableName(table, epoch);
        var activeView = GetActiveViewName(table);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 07 Zero-Row Protection Guard:
            // A shadow generation with 0 rows MUST NOT replace a healthy active generation with >0 rows
            if (_metadata.TryGetValue(table, out var existing) && existing.RowCount > 0 && rowCount == 0)
            {
                _logger?.LogWarning("SEC-BL04-07: Aborted swap for table '{Table}' because shadow staging has 0 rows while active generation has {ActiveRows} rows.", table, existing.RowCount);
                throw new InvalidOperationException($"Zero-state protection: shadow staging table '{shadow}' returned 0 rows but active generation has {existing.RowCount} rows.");
            }

            using var cmd = _sharedConnection.CreateCommand();
            cmd.CommandText = $"CREATE OR REPLACE VIEW \"{activeView}\" AS SELECT * FROM \"{shadow}\";";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

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
                StorageTier: TableCacheStorageTier.Memory,
                InternalStoragePath: $":memory:{activeView}",
                ActiveViewName: activeView,
                GenerationEpoch: epoch,
                LastRefreshedAt: DateTimeOffset.UtcNow,
                ExpiresAt: expiresAt,
                RowCount: rowCount,
                SizeInBytes: sizeInBytes,
                LastWatermarkOffset: null,
                IsHealthy: true,
                LastError: null,
                CachedColumns: cachedCols,
                FilterPredicate: existing?.FilterPredicate,
                OfflinePolicy: existing?.OfflinePolicy ?? TableCacheOfflinePolicy.FailClosed,
                PrimaryKeyColumns: pkCols);

            _metadata[table] = meta;
            _activeBackingTables[table] = shadow;
            return meta;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DropGenerationAsync(TableIdentifier table, long epoch, CancellationToken ct = default)
    {
        var shadowTable = GetShadowTableName(table, epoch);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _sharedConnection.CreateCommand();
            cmd.CommandText = $"DROP TABLE IF EXISTS \"{shadowTable}\";";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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

        if (!_metadata.TryGetValue(table, out var meta))
        {
            var firstOp = mutations[0]?.Operation.ToString() ?? "Unknown";
            _logger?.LogError(
                "Table cache mutation batch rejected for table '{Table}' on operation {Operation} ({ErrorType}, count={MutationCount}): active cached table missing.",
                table,
                firstOp,
                nameof(InvalidOperationException),
                mutations.Count);
            await InvalidateTableOnFailureAsync(table, "Table cache entry unavailable during CDC mutation.", ct).ConfigureAwait(false);
            throw new InvalidOperationException($"No active table cache entry available for '{table}'.");
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

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var txCmd = _sharedConnection.CreateCommand();
            txCmd.CommandText = "BEGIN TRANSACTION;";
            await txCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            try
            {
                foreach (var step in planned)
                {
                    failedOp = step.Operation;
                    using var cmd = _sharedConnection.CreateCommand();
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
            _lock.Release();
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
        await Task.CompletedTask;
        if (!_metadata.ContainsKey(table))
        {
            throw new KeyNotFoundException($"No active in-memory table cache entry available for '{table}'.");
        }

        return _sharedConnection;
    }

    public void ReturnReaderConnection(TableIdentifier table, DbConnection connection)
    {
        // No-op for shared in-memory DuckDB connection
    }

    public async Task EvictAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var activeView = GetActiveViewName(table);
        _activeBackingTables.TryRemove(table, out var backingTable);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _sharedConnection.CreateCommand();
            cmd.CommandText = $"DROP VIEW IF EXISTS \"{activeView}\";";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(backingTable))
            {
                cmd.CommandText = $"DROP TABLE IF EXISTS \"{backingTable}\";";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            _metadata.TryRemove(table, out _);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sharedConnection.Dispose();
        _lock.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
