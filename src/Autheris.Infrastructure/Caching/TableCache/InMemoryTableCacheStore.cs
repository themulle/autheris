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
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Volatile in-memory DuckDB table cache store.
/// Provides sub-millisecond query execution over in-memory columnar tables.
/// Conforms to SEC-BL04-07, SEC-BL04-11, and SEC-BL04-14.
/// </summary>
public sealed class InMemoryTableCacheStore : ITableCacheStore, IDisposable
{
    private static readonly SearchValues<char> ValidIdentifierChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_");

    private readonly TableCacheOptions _options;
    private readonly ILogger<InMemoryTableCacheStore>? _logger;
    private readonly DuckDBConnection _sharedConnection;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<TableIdentifier, TableCacheEntryMetadata> _metadata = new();
    private bool _disposed;

    public TableCacheStorageTier StorageTier => TableCacheStorageTier.Memory;

    public InMemoryTableCacheStore(
        IOptions<TableCacheOptions> options,
        ILogger<InMemoryTableCacheStore>? logger = null)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger;
        _sharedConnection = new DuckDBConnection("DataSource=:memory:");
        _sharedConnection.Open();

        using var cmd = _sharedConnection.CreateCommand();
        cmd.CommandText = $"PRAGMA max_memory='{_options.DefaultMemoryLimitMb}MB'; PRAGMA threads=4;";
        cmd.ExecuteNonQuery();
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
            throw new ArgumentException($"Identifier contains invalid characters. Only alphanumeric and underscore allowed: '{identifier}'", nameof(identifier));
        }

        return trimmed;
    }

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

            // Create shadow table with VARCHAR columns for flexible ingestion, or inferred schema
            var colDefs = string.Join(", ", sanitizedCols.Select(c => $"\"{c}\" VARCHAR"));
            cmd.CommandText = $"CREATE TABLE \"{shadowTable}\" ({colDefs});";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // Ingest rows
            var insertBase = $"INSERT INTO \"{shadowTable}\" ({string.Join(", ", sanitizedCols.Select(c => $"\"{c}\""))}) VALUES ";
            var batch = new List<IReadOnlyList<object?>>(1000);

            await foreach (var row in rows.WithCancellation(ct).ConfigureAwait(false))
            {
                batch.Add(row);
                if (batch.Count >= 1000)
                {
                    await InsertBatchAsync(insertBase, sanitizedCols.Count, batch, ct).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await InsertBatchAsync(insertBase, sanitizedCols.Count, batch, ct).ConfigureAwait(false);
                batch.Clear();
            }

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
        var shadow = GetShadowTableName(table, epoch);
        var activeView = GetActiveViewName(table);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // SEC-BL04-07 Zero-Row Protection Guard:
            // A shadow generation with 0 rows MUST NOT replace a healthy active generation with >0 rows
            if (_metadata.TryGetValue(table, out var existing) && existing.RowCount > 0 && rowCount == 0)
            {
                _logger?.LogWarning("SEC-BL04-07: Aborted swap for table '{Table}' because shadow staging has 0 rows while active generation has {ActiveRows} rows.", table, existing.RowCount);
                throw new InvalidOperationException($"Zero-state protection: shadow staging table '{shadow}' returned 0 rows but active generation has {existing.RowCount} rows.");
            }

            using var cmd = _sharedConnection.CreateCommand();
            cmd.CommandText = $"CREATE OR REPLACE VIEW \"{activeView}\" AS SELECT * FROM \"{shadow}\";";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

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
                CachedColumns: existing?.CachedColumns ?? [],
                FilterPredicate: existing?.FilterPredicate,
                OfflinePolicy: existing?.OfflinePolicy ?? TableCacheOfflinePolicy.FailClosed);

            _metadata[table] = meta;
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
        ArgumentNullException.ThrowIfNull(mutations);

        if (mutations.Count == 0) return;
        var activeView = GetActiveViewName(table);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var mut in mutations)
            {
                using var cmd = _sharedConnection.CreateCommand();
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
                            _logger?.LogWarning(ex, "Failed applying insert mutation to '{Table}'", table);
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
                            _logger?.LogWarning(ex, "Failed applying update mutation to '{Table}'", table);
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
                            _logger?.LogWarning(ex, "Failed applying delete mutation to '{Table}'", table);
                        }
                        break;
                    }
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DbConnection> LeaseReaderConnectionAsync(TableIdentifier table, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await Task.CompletedTask;
        return _sharedConnection;
    }

    public void ReturnReaderConnection(TableIdentifier table, DbConnection connection)
    {
        // No-op for shared in-memory DuckDB connection
    }

    public async Task EvictAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var activeView = GetActiveViewName(table);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _sharedConnection.CreateCommand();
            cmd.CommandText = $"DROP VIEW IF EXISTS \"{activeView}\";";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
