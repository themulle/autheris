using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// Hands out short-lived, pooled SQLite connections for the governance repository (WP-ARC-B-13) instead of one shared connection. The
/// constructor performs no I/O; the first <see cref="OpenAsync"/> creates the directory, keeps the in-memory database alive and applies
/// the journal mode. A file database never uses a shared cache; an in-memory database always does (one named, shared-cache database per
/// provider that lives as long as the provider).
/// </summary>
internal sealed class SqliteGovernanceConnectionProvider : IAsyncDisposable, IDisposable
{
    private const int BusyTimeoutMilliseconds = 5000;
    private const int DefaultCommandTimeoutSeconds = 30;
    private const int CacheSizeKibibytes = 8000;

    private readonly string _connectionString;
    private readonly string _journalMode;
    private readonly ILogger? _logger;
    private readonly Func<SqliteConnection, string, string> _journalModeApplier;
    private readonly SemaphoreSlim _setupLock = new(1, 1);
    private SqliteConnection? _memorySentinel;
    private volatile bool _setupDone;
    private volatile string _effectiveJournalMode;
    private int _disposed;

    public SqliteGovernanceConnectionProvider(
        string connectionString,
        string? journalMode,
        ILogger? logger,
        Func<SqliteConnection, string, string>? journalModeApplier = null,
        bool allowUnsafeJournalModes = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _logger = logger;
        _journalModeApplier = journalModeApplier ?? ApplyJournalMode;
        _journalMode = NormalizeJournalMode(journalMode);

        // OFF and MEMORY give up crash safety of the audit chain and the consent state; only Development may choose them.
        if (!allowUnsafeJournalModes && _journalMode is "OFF" or "MEMORY")
        {
            throw new InvalidOperationException($"Security critical: GovernanceDb:JournalMode '{_journalMode}' is not allowed outside Development (no crash safety).");
        }

        _effectiveJournalMode = _journalMode;

        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;
        var isMemory = builder.Mode == SqliteOpenMode.Memory ||
                       string.IsNullOrWhiteSpace(dataSource) ||
                       dataSource.StartsWith(":memory:", StringComparison.OrdinalIgnoreCase) ||
                       dataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase);

        if (isMemory)
        {
            // A private in-memory database would be a different, empty database on every pooled connection. Give each provider its
            // own named shared-cache database instead of the process-global ":memory:" one.
            if (string.IsNullOrWhiteSpace(dataSource) || dataSource.StartsWith(":memory:", StringComparison.OrdinalIgnoreCase))
            {
                builder.DataSource = $"governance_{Guid.NewGuid():N}";
            }

            builder.Mode = SqliteOpenMode.Memory;
            builder.Cache = SqliteCacheMode.Shared;
        }
        else
        {
            // Shared cache serializes readers behind writers (table locks) and is discouraged by SQLite; WAL gives concurrent readers.
            builder.Cache = SqliteCacheMode.Default;
        }

        builder.Pooling = true;
        builder.DefaultTimeout = DefaultCommandTimeoutSeconds;
        IsMemory = isMemory;
        _connectionString = builder.ConnectionString;
    }

    /// <summary>True for an in-memory database (readers must then share the write gate: shared-cache table locks).</summary>
    public bool IsMemory { get; }

    /// <summary>Opens a pooled connection with the operational pragmas applied. Disposing the connection returns it to the pool.</summary>
    public async ValueTask<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_setupDone)
        {
            await SetupAsync(ct).ConfigureAwait(false);
        }

        return await OpenConfiguredAsync(ct).ConfigureAwait(false);
    }

    private async Task SetupAsync(CancellationToken ct)
    {
        await _setupLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_setupDone)
            {
                return;
            }

            if (IsMemory)
            {
                // The in-memory database exists while at least one connection is open; the sentinel keeps it for the provider lifetime.
                var sentinel = new SqliteConnection(_connectionString);
                try
                {
                    await sentinel.OpenAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    await sentinel.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                _memorySentinel = sentinel;
            }
            else
            {
                EnsureDirectoryExists();
                await using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync(ct).ConfigureAwait(false);
                await ExecuteAsync(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};", ct).ConfigureAwait(false);
                ApplyJournalModeWithFallback(connection);
            }

            _setupDone = true;
        }
        finally
        {
            _setupLock.Release();
        }
    }

    private void EnsureDirectoryExists()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        var directory = Path.GetDirectoryName(builder.DataSource);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    // R-42: some filesystems (bind mounts from Windows/WSL, network shares) cannot provide the WAL shared memory; fall back to DELETE.
    private void ApplyJournalModeWithFallback(SqliteConnection connection)
    {
        try
        {
            // The pragma reports the mode that is actually in effect (a filesystem without shared memory silently keeps DELETE).
            var effective = _journalModeApplier(connection, _journalMode).ToUpperInvariant();
            if (effective != _journalMode)
            {
                _logger?.LogWarning("Requested journal_mode={Requested} but SQLite reports {Effective}.", _journalMode, effective);
            }

            _effectiveJournalMode = effective;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to apply PRAGMA journal_mode={JournalMode}; attempting graceful fallback to DELETE mode (R-42).", _journalMode);
            try
            {
                _effectiveJournalMode = _journalModeApplier(connection, "DELETE").ToUpperInvariant();
            }
            catch (Exception fallbackException)
            {
                _logger?.LogError(fallbackException, "Graceful fallback to journal_mode=DELETE also failed.");
                _effectiveJournalMode = "DELETE";
            }
        }
    }

    private async ValueTask<SqliteConnection> OpenConfiguredAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            if (IsMemory)
            {
                // Nothing to tune (no file, no fsync, no cross-process busy wait). Preparing a pragma on a shared-cache connection while
                // another connection holds an uncommitted schema write (a migrator running DDL) fails inside sqlite3_prepare_v2.
                return connection;
            }

            var synchronous = _effectiveJournalMode == "WAL" ? "NORMAL" : "FULL";
            await ExecuteAsync(
                connection,
                $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds}; PRAGMA synchronous = {synchronous}; PRAGMA temp_store = MEMORY; PRAGMA cache_size = -{CacheSizeKibibytes};",
                ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string ApplyJournalMode(SqliteConnection connection, string mode)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA journal_mode = {mode};";
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? mode;
    }

    private static string NormalizeJournalMode(string? configured)
    {
        var mode = string.IsNullOrWhiteSpace(configured) ? "WAL" : configured.Trim().ToUpperInvariant();
        return mode is "WAL" or "DELETE" or "TRUNCATE" or "MEMORY" or "OFF" ? mode : "WAL";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _memorySentinel?.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to dispose the in-memory governance database sentinel.");
        }

        try
        {
            using var poolKey = new SqliteConnection(_connectionString);
            SqliteConnection.ClearPool(poolKey);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to clear the governance SQLite connection pool.");
        }

        _setupLock.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
