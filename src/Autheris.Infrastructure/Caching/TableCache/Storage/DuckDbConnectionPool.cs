namespace Autheris.Infrastructure.Caching.TableCache.Storage;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;

/// <summary>
/// Manages leased reader and writer connections to DuckDB storage instances.
/// Ensures safe multi-reader single-writer semantics and bounds connection concurrency.
/// Supports initialization/attach SQL for encrypted native databases.
/// </summary>
public sealed class DuckDbConnectionPool : IDisposable
{
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(5);

    private readonly string _connectionString;
    private readonly string? _attachSql;
    private readonly int _maxIdleReaders;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writerLock = new(1, 1);
    private readonly SemaphoreSlim _readerSemaphore;
    private readonly ConcurrentDictionary<DbConnection, byte> _leasedReaders = new(ReferenceEqualityComparer.Instance);
    private readonly object _idleLock = new();
    private readonly List<(DuckDBConnection Conn, long ReturnedTimestamp)> _idleReaders = [];
    private int _disposed;

    public DuckDbConnectionPool(
        string connectionString,
        string? attachSql = null,
        int maxConcurrentReaders = 16,
        int maxIdleReaders = 8,
        TimeSpan? idleTimeout = null,
        TimeProvider? timeProvider = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _attachSql = attachSql;
        int effectiveMaxReaders = Math.Max(1, maxConcurrentReaders);
        _maxIdleReaders = Math.Clamp(maxIdleReaders, 0, effectiveMaxReaders);
        _idleTimeout = idleTimeout is { } t && t > TimeSpan.Zero ? t : DefaultIdleTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _readerSemaphore = new SemaphoreSlim(effectiveMaxReaders, effectiveMaxReaders);
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    internal int IdleCount { get { lock (_idleLock) return _idleReaders.Count; } }
    internal int LeasedCount => _leasedReaders.Count;
    internal int AvailableReaderSlots => _readerSemaphore.CurrentCount;

    public async Task<DbConnection> LeaseReaderAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _readerSemaphore.WaitAsync(ct).ConfigureAwait(false);
        bool handedOff = false;

        try
        {
            ThrowIfDisposed();

            while (TryTakeValidIdleReader(out var idleConn))
            {
                if (idleConn!.State == System.Data.ConnectionState.Open)
                {
                    _leasedReaders[idleConn] = 0;
                    handedOff = true;
                    return idleConn;
                }

                idleConn.Dispose();
            }

            var newConn = new DuckDBConnection(_connectionString);
            try
            {
                await newConn.OpenAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(_attachSql))
                {
                    using var cmd = newConn.CreateCommand();
                    cmd.CommandText = _attachSql;
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                _leasedReaders[newConn] = 0;
                handedOff = true;
                return newConn;
            }
            catch
            {
                newConn.Dispose();
                throw;
            }
        }
        finally
        {
            if (!handedOff && !IsDisposed)
            {
                try { _readerSemaphore.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }

    public void ReturnReader(DbConnection conn)
    {
        ArgumentNullException.ThrowIfNull(conn);
        bool wasLeased = _leasedReaders.TryRemove(conn, out _);

        try
        {
            if (IsDisposed || !wasLeased || conn is not DuckDBConnection duckConn || conn.State != System.Data.ConnectionState.Open || _maxIdleReaders == 0)
            {
                conn.Dispose();
                return;
            }

            List<DuckDBConnection>? expired = null;
            bool added = false;

            lock (_idleLock)
            {
                if (!IsDisposed)
                {
                    long now = _timeProvider.GetTimestamp();
                    expired = CollectExpiredLocked(now);
                    if (_idleReaders.Count < _maxIdleReaders)
                    {
                        _idleReaders.Add((duckConn, now));
                        added = true;
                    }
                }
            }

            DisposeAll(expired);
            if (!added)
            {
                duckConn.Dispose();
            }
        }
        finally
        {
            if (wasLeased && !IsDisposed)
            {
                try { _readerSemaphore.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }

    internal void TrimIdle()
    {
        List<DuckDBConnection>? expired;
        lock (_idleLock) expired = CollectExpiredLocked(_timeProvider.GetTimestamp());
        DisposeAll(expired);
    }

    private bool TryTakeValidIdleReader(out DuckDBConnection? conn)
    {
        List<DuckDBConnection>? expired;
        conn = null;
        lock (_idleLock)
        {
            expired = CollectExpiredLocked(_timeProvider.GetTimestamp());
            if (_idleReaders.Count > 0)
            {
                int last = _idleReaders.Count - 1;
                conn = _idleReaders[last].Conn;
                _idleReaders.RemoveAt(last);
            }
        }
        DisposeAll(expired);
        return conn != null;
    }

    private List<DuckDBConnection>? CollectExpiredLocked(long now)
    {
        List<DuckDBConnection>? expired = null;
        int removeCount = 0;
        while (removeCount < _idleReaders.Count &&
               _timeProvider.GetElapsedTime(_idleReaders[removeCount].ReturnedTimestamp, now) >= _idleTimeout)
        {
            (expired ??= []).Add(_idleReaders[removeCount].Conn);
            removeCount++;
        }
        if (removeCount > 0) _idleReaders.RemoveRange(0, removeCount);
        return expired;
    }

    private static void DisposeAll(List<DuckDBConnection>? connections)
    {
        if (connections == null) return;
        foreach (var c in connections) { try { c.Dispose(); } catch { } }
    }

    public async Task<IDisposable> AcquireWriterLockAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _writerLock.WaitAsync(ct).ConfigureAwait(false);
        return new WriterReleaser(_writerLock);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        List<DuckDBConnection> toDispose;
        lock (_idleLock)
        {
            toDispose = [.. _idleReaders.ConvertAll(x => x.Conn)];
            _idleReaders.Clear();
        }
        DisposeAll(toDispose);
        _writerLock.Dispose();
        _readerSemaphore.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    private sealed class WriterReleaser(SemaphoreSlim sem) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) sem.Release(); }
    }
}
