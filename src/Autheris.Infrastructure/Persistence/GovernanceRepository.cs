namespace Autheris.Infrastructure.Persistence;

using System;
using System.Data.Common;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Unified governance repository.
/// Persists and queries governance metadata across supported engines using <see cref="IGovernanceSqlDialect"/>.
/// Enforces tenant scoping, safe parameterization, and confidentiality in logging.
/// </summary>
public partial class GovernanceRepository : IConsentRepository, IConsentApprovalRepository, IConsentDescriptionRepository
{
    private static readonly long MetadataCacheTtlTicks = TimeSpan.FromSeconds(30).Ticks;

    // Bounded (a 10k-table catalog must not grow the cache without limit); a miss just reloads the metadata.
    private readonly Autheris.Application.State.Caching.BoundedCache<string, (TableMetadata? Metadata, long CachedAtTicks, long Epoch)> _metadataCache = new(20_000, comparer: StringComparer.OrdinalIgnoreCase);
    private readonly CatalogKeyDefinitionStore.KeyTableProbe _keyTables = new();
    private readonly IGovernanceSqlDialect _dialect;
    private readonly ILogger? _logger;
    private readonly IEventBus? _eventBus;
    private readonly GatewayOptions? _options;
    private readonly IEpochValidationService? _epochValidationService;
    private readonly Autheris.Application.Identity.Attributes.IConsentMaterializationGuard? _materializationGuard;
    private readonly Func<AuditLogEntry, DbTransaction?, CancellationToken, Task>? _auditRecorder;
    private readonly Action<string>? _metadataInvalidator;
    private readonly Action<DbTransaction>? _onTransactionCommitted;
    private readonly Action<DbTransaction>? _onTransactionRolledBack;

    /// <summary>
    /// Test seam: runs inside decision transactions between locked read and guarded state transition.
    /// </summary>
    internal Func<DbConnection, DbTransaction, CancellationToken, Task>? ConsentStatusUpdateSeam { get; set; }

    internal GovernanceRepository(
        IGovernanceSqlDialect dialect,
        IEpochValidationService? epochValidationService = null,
        IOptions<GatewayOptions>? options = null,
        ILogger? logger = null,
        Autheris.Application.Identity.Attributes.IConsentMaterializationGuard? materializationGuard = null,
        Func<AuditLogEntry, DbTransaction?, CancellationToken, Task>? auditRecorder = null,
        Action<string>? metadataInvalidator = null,
        Action<DbTransaction>? onTransactionCommitted = null,
        Action<DbTransaction>? onTransactionRolledBack = null,
        IEventBus? eventBus = null)
    {
        _eventBus = eventBus;
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        _epochValidationService = epochValidationService;
        _options = options?.Value;
        _logger = logger;
        _materializationGuard = materializationGuard;
        _auditRecorder = auditRecorder;
        _metadataInvalidator = metadataInvalidator;
        _onTransactionCommitted = onTransactionCommitted;
        _onTransactionRolledBack = onTransactionRolledBack;
    }

    internal IGovernanceSqlDialect Dialect => _dialect;

    internal Autheris.Application.State.Caching.BoundedCache<string, (TableMetadata? Metadata, long CachedAtTicks, long Epoch)> MetadataCache => _metadataCache;

    /// <summary>Test seam: runs after a metadata read loaded its snapshot and before the result is cached.</summary>
    internal Func<CancellationToken, Task>? AfterMetadataSnapshotForTesting { get; set; }

    /// <summary>Test seam: a positive key-table probe is cached per instance; this models a fresh instance.</summary>
    internal void ResetKeyTableProbeForTesting() => _keyTables.Reset();

    public async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var conn = _dialect.CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    public DbConnection OpenConnection()
    {
        var conn = _dialect.CreateConnection();
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Executes a reader command, logging fragment-id and duration only.
    /// Never logs parameter values or full command text with parameters.
    /// </summary>
    internal async Task<DbDataReader> ExecuteReaderAsync(DbCommand command, string fragmentId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(fragmentId);

        var sw = Stopwatch.StartNew();
        try
        {
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            sw.Stop();
            _logger?.LogDebug("Executed {FragmentId} in {ElapsedMs}ms", fragmentId, sw.ElapsedMilliseconds);
            return reader;
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogExecutionFailure(ex, fragmentId, sw.ElapsedMilliseconds);
            throw;
        }
    }

    /// <summary>
    /// Executes a non-query command, logging fragment-id and duration only.
    /// </summary>
    internal async Task<int> ExecuteNonQueryAsync(DbCommand command, string fragmentId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(fragmentId);

        var sw = Stopwatch.StartNew();
        try
        {
            var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            sw.Stop();
            _logger?.LogDebug("Executed {FragmentId} in {ElapsedMs}ms: {RowCount} rows affected", fragmentId, sw.ElapsedMilliseconds, rows);
            return rows;
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogExecutionFailure(ex, fragmentId, sw.ElapsedMilliseconds);
            throw;
        }
    }

    /// <summary>
    /// Executes a scalar command, logging fragment-id and duration only.
    /// </summary>
    internal async Task<object?> ExecuteScalarAsync(DbCommand command, string fragmentId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(fragmentId);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            sw.Stop();
            _logger?.LogDebug("Executed {FragmentId} in {ElapsedMs}ms", fragmentId, sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogExecutionFailure(ex, fragmentId, sw.ElapsedMilliseconds);
            throw;
        }
    }

    // Only exception type and engine error code are logged; never the exception object (Message/Detail may echo values).
    private void LogExecutionFailure(Exception ex, string fragmentId, long elapsedMs)
    {
        string code = ex is Npgsql.PostgresException pg ? pg.SqlState : ex is Microsoft.Data.SqlClient.SqlException sql ? sql.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "n/a";
        _logger?.LogError("Execution failed for {FragmentId} after {ElapsedMs}ms ({ExceptionType}, code {ErrorCode})", fragmentId, elapsedMs, ex.GetType().Name, code);
    }

    internal async Task CommitTransactionAsync(DbTransaction tx, CancellationToken ct)
    {
        await tx.CommitAsync(ct).ConfigureAwait(false);
        _onTransactionCommitted?.Invoke(tx);
    }

    internal async Task RollbackTransactionAsync(DbTransaction tx, CancellationToken ct = default)
    {
        try
        {
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
        }
        finally
        {
            _onTransactionRolledBack?.Invoke(tx);
        }
    }

    private void InvalidateTableMetadata(string tableKey)
    {
        _metadataCache.TryRemove(tableKey, out _);
        _metadataInvalidator?.Invoke(tableKey);
    }

    private async Task RecordAuditEventAsync(AuditLogEntry entry, DbTransaction? tx, CancellationToken ct)
    {
        if (_auditRecorder != null)
        {
            await _auditRecorder(entry, tx, ct).ConfigureAwait(false);
        }
    }

    internal async Task<long> IncrementTableEpochInternalAsync(DbConnection conn, DbTransaction tx, TableIdentifier table, CancellationToken ct)
    {
        InvalidateTableMetadata(table.ToString().ToLowerInvariant());
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            // Transitional: replace engine-type branch with dialect members once the epoch statements move into the dialect.
            if (_dialect is PostgreSqlGovernanceDialect)
            {
                cmd.CommandText = @"UPDATE POLICY_EPOCHS
                                    SET epoch = epoch + 1, updated_at = @now
                                    WHERE LOWER(domain) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)
                                    RETURNING epoch";
            }
            else
            {
                cmd.CommandText = @"UPDATE POLICY_EPOCHS
                                    SET epoch = epoch + 1, updated_at = @now
                                    OUTPUT inserted.epoch
                                    WHERE LOWER(domain) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)";
            }
            cmd.AddBound("@domain", table.Domain);
            cmd.AddBound("@schema", table.Schema);
            cmd.AddBound("@table", table.TableName);
            cmd.AddBound("@now", TimeProvider.System.GetUtcNow().ToString("O"));

            var scalar = await ExecuteScalarAsync(cmd, "Epochs.IncrementExisting", ct).ConfigureAwait(false);
            if (scalar != null && scalar != DBNull.Value)
            {
                return Convert.ToInt64(scalar);
            }
        }

        await using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.Transaction = tx;
            // Transitional: replace engine-type branch with dialect members once the epoch statements move into the dialect.
            if (_dialect is PostgreSqlGovernanceDialect)
            {
                insertCmd.CommandText = @"INSERT INTO POLICY_EPOCHS (table_id, domain, schema_name, table_name, epoch, updated_at)
                                          SELECT id, source_name, schema_name, table_name, 2, @now
                                          FROM TABLES
                                          WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)
                                          ON CONFLICT (table_id) DO UPDATE SET epoch = POLICY_EPOCHS.epoch + 1, updated_at = EXCLUDED.updated_at
                                          RETURNING epoch";
            }
            else
            {
                insertCmd.CommandText = @"MERGE POLICY_EPOCHS WITH (HOLDLOCK) AS target
                                          USING (SELECT id AS table_id, source_name AS domain, schema_name, table_name FROM TABLES
                                                 WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)) AS source
                                          ON target.table_id = source.table_id
                                          WHEN MATCHED THEN UPDATE SET epoch = target.epoch + 1, updated_at = @now
                                          WHEN NOT MATCHED THEN INSERT (table_id, domain, schema_name, table_name, epoch, updated_at)
                                              VALUES (source.table_id, source.domain, source.schema_name, source.table_name, 2, @now)
                                          OUTPUT inserted.epoch;";
            }
            insertCmd.AddBound("@domain", table.Domain);
            insertCmd.AddBound("@schema", table.Schema);
            insertCmd.AddBound("@table", table.TableName);
            insertCmd.AddBound("@now", TimeProvider.System.GetUtcNow().ToString("O"));

            var insScalar = await ExecuteScalarAsync(insertCmd, "Epochs.InsertOrIncrement", ct).ConfigureAwait(false);
            if (insScalar != null && insScalar != DBNull.Value)
            {
                return Convert.ToInt64(insScalar);
            }
        }

        return 0;
    }

    private async Task InvalidateTableEpochAsync(TableIdentifier table, CancellationToken ct)
    {
        InvalidateTableMetadata(table.ToString().ToLowerInvariant());
        if (_epochValidationService != null)
        {
            await _epochValidationService.InvalidateEpochAsync(table, ct).ConfigureAwait(false);
        }
    }
}
