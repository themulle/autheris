namespace Autheris.Infrastructure.Caching.TableCache.Storage;

using System;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;

/// <summary>
/// Manages leased reader and writer connections to DuckDB storage instances.
/// Ensures safe multi-reader single-writer semantics and bounds connection concurrency.
/// </summary>
public sealed class DuckDbConnectionPool : IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writerLock = new(1, 1);
    private readonly ConcurrentBag<DuckDBConnection> _idleReaders = new();
    private bool _disposed;

    public DuckDbConnectionPool(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task<DbConnection> LeaseReaderAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (_idleReaders.TryTake(out var conn))
        {
            if (conn.State == System.Data.ConnectionState.Open)
            {
                return conn;
            }

            conn.Dispose();
        }

        var newConn = new DuckDBConnection(_connectionString);
        await newConn.OpenAsync(ct).ConfigureAwait(false);
        return newConn;
    }

    public void ReturnReader(DbConnection conn)
    {
        if (_disposed || conn is not DuckDBConnection duckConn || conn.State != System.Data.ConnectionState.Open)
        {
            conn.Dispose();
            return;
        }

        _idleReaders.Add(duckConn);
    }

    public async Task<IDisposable> AcquireWriterLockAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _writerLock.WaitAsync(ct).ConfigureAwait(false);
        return new WriterReleaser(_writerLock);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        while (_idleReaders.TryTake(out var conn))
        {
            try { conn.Dispose(); } catch { }
        }

        _writerLock.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class WriterReleaser(SemaphoreSlim sem) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                sem.Release();
            }
        }
    }
}
