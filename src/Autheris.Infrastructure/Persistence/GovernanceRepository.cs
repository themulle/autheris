namespace Autheris.Infrastructure.Persistence;

using System;
using System.Data.Common;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
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
public partial class GovernanceRepository : IGovernanceRepository, IConsentDescriptionRepository, Migrations.IGovernanceStoreInitializer, IAuditChainExportSource, IAuditPurgeKeyProvider
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
    private readonly Microsoft.Extensions.Hosting.IHostEnvironment? _environment;
    private int _initialized;
    private volatile bool _ready;

    /// <summary>
    /// Test seam: runs inside decision transactions between locked read and guarded state transition.
    /// </summary>
    internal Func<DbConnection, DbTransaction, CancellationToken, Task>? ConsentStatusUpdateSeam { get; set; }

    public GovernanceRepository(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions> options,
        Microsoft.Extensions.Hosting.IHostEnvironment environment,
        IKeyVaultSecretProvider secretProvider,
        Autheris.Application.Audit.IAuditChainAnchorStore auditAnchorStore,
        ICallerContextProvider callerContextProvider,
        ILogger<GovernanceRepository>? logger = null,
        IEventBus? eventBus = null,
        Autheris.Application.Identity.Attributes.IConsentMaterializationGuard? materializationGuard = null)
        : this(
            ResolveDialectForLegacyCtor(options),
            epochValidationService ?? throw new ArgumentNullException(nameof(epochValidationService)),
            options ?? throw new ArgumentNullException(nameof(options)),
            environment ?? throw new ArgumentNullException(nameof(environment)),
            secretProvider ?? throw new ArgumentNullException(nameof(secretProvider)),
            auditAnchorStore ?? throw new ArgumentNullException(nameof(auditAnchorStore)),
            callerContextProvider ?? throw new ArgumentNullException(nameof(callerContextProvider)),
            logger,
            eventBus,
            materializationGuard)
    {
    }

    private static IGovernanceSqlDialect ResolveDialectForLegacyCtor(IOptions<GatewayOptions>? options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var connStr = options.Value?.GovernanceDb?.ConnectionString ?? string.Empty;
        return options.Value?.GovernanceDb?.Provider?.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) == true
            ? new SqlServerGovernanceDialect(connStr)
            : new PostgreSqlGovernanceDialect(connStr);
    }

    public GovernanceRepository(
        IGovernanceSqlDialect dialect,
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions> options,
        Microsoft.Extensions.Hosting.IHostEnvironment environment,
        IKeyVaultSecretProvider secretProvider,
        Autheris.Application.Audit.IAuditChainAnchorStore auditAnchorStore,
        ICallerContextProvider callerContextProvider,
        ILogger<GovernanceRepository>? logger = null,
        IEventBus? eventBus = null,
        Autheris.Application.Identity.Attributes.IConsentMaterializationGuard? materializationGuard = null)
        : this(dialect,
               epochValidationService ?? throw new ArgumentNullException(nameof(epochValidationService)),
               options ?? throw new ArgumentNullException(nameof(options)),
               (ILogger?)logger,
               materializationGuard,
               auditRecorder: null,
               metadataInvalidator: null,
               onTransactionCommitted: null,
               onTransactionRolledBack: null,
               eventBus,
               auditAnchorStore ?? throw new ArgumentNullException(nameof(auditAnchorStore)),
               secretProvider ?? throw new ArgumentNullException(nameof(secretProvider)),
               environment ?? throw new ArgumentNullException(nameof(environment)),
               callerContextProvider ?? throw new ArgumentNullException(nameof(callerContextProvider)))
    {
    }

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
        IEventBus? eventBus = null,
        Autheris.Application.Audit.IAuditChainAnchorStore? auditAnchorStore = null,
        IKeyVaultSecretProvider? secretProvider = null,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null,
        ICallerContextProvider? callerContextProvider = null)
    {
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        _eventBus = eventBus;
        _epochValidationService = epochValidationService;
        _options = options?.Value;
        _logger = logger;
        _materializationGuard = materializationGuard;
        _auditRecorder = auditRecorder;
        _metadataInvalidator = metadataInvalidator;
        _onTransactionCommitted = onTransactionCommitted;
        _onTransactionRolledBack = onTransactionRolledBack;
        _environment = environment;
        _ready = environment == null;
        InitializeAuditFields(options, auditAnchorStore, secretProvider, environment, callerContextProvider);
    }

    internal IGovernanceSqlDialect Dialect => _dialect;

    internal Autheris.Application.State.Caching.BoundedCache<string, (TableMetadata? Metadata, long CachedAtTicks, long Epoch)> MetadataCache => _metadataCache;

    internal string? MigrationConnectionString => _options?.GovernanceDb?.MigrationConnectionString;

    internal void InvalidateMetadataCacheForTesting() => _metadataCache.Clear();

    /// <summary>Test seam: runs after a metadata read loaded its snapshot and before the result is cached.</summary>
    internal Func<CancellationToken, Task>? AfterMetadataSnapshotForTesting { get; set; }

    /// <summary>Test seam: a positive key-table probe is cached per instance; this models a fresh instance.</summary>
    internal void ResetKeyTableProbeForTesting() => _keyTables.Reset();

    private void ThrowIfNotReady()
    {
        if (!_ready)
        {
            throw new Migrations.GovernanceStoreNotReadyException(nameof(GovernanceRepository));
        }
    }

    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return Task.CompletedTask;
        }

        try
        {
            InitializeAudit();
            var shouldSeed = _options?.GovernanceDb?.SeedDemoData ?? (_environment != null && (string.Equals(_environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase) || string.Equals(_environment.EnvironmentName, "Test", StringComparison.OrdinalIgnoreCase)));
            if (shouldSeed)
            {
                SeedInitialCatalog();
            }

            _ready = true;
        }
        catch
        {
            Volatile.Write(ref _initialized, 0);
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (!_ready)
        {
            return false;
        }

        try
        {
            await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1;";
            var res = await ExecuteScalarAsync(cmd, "Governance.Ping", ct).ConfigureAwait(false);
            return res != null;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        ThrowIfNotReady();
        var conn = _dialect.CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    /// <summary>
    /// Opens an unchecked connection directly from the dialect, used for schema migrations and initialization
    /// before the store is marked ready.
    /// </summary>
    internal async ValueTask<DbConnection> OpenUncheckedConnectionAsync(CancellationToken ct = default)
    {
        var conn = _dialect.CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    /// <summary>
    /// Opens an unchecked connection directly from the dialect, used for schema migrations and initialization
    /// before the store is marked ready.
    /// </summary>
    internal DbConnection OpenUncheckedConnection()
    {
        var conn = _dialect.CreateConnection();
        conn.Open();
        return conn;
    }

    public DbConnection OpenConnection()
    {
        ThrowIfNotReady();
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
        OnTransactionCommitted(tx);
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
            OnTransactionRolledBack(tx);
        }
    }

    private void InvalidateTableMetadata(string tableKey)
    {
        _metadataCache.TryRemove(tableKey, out _);
        _metadataInvalidator?.Invoke(tableKey);
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

/// <summary>
/// Parameter binding shared by the engine-neutral SQL cores.
/// Values are always bound parameters; nothing here concatenates caller data into SQL.
/// </summary>
internal static class SqlCoreBinding
{
    /// <summary>
    /// DT-SC-04: a tenant parameter never binds NULL, empty or whitespace.
    /// </summary>
    internal static void GuardTenantParameter(string name, object? value)
    {
        if ((string.Equals(name, "@tenantId", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "@tenant", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "@tenant_id", StringComparison.OrdinalIgnoreCase))
            && (value is null || value is DBNull || (value is string text && string.IsNullOrWhiteSpace(text))))
        {
            throw new ArgumentException("A tenant is required; default(TenantId) is not a tenant.", nameof(value));
        }
    }

    public static DbParameter AddBound(this DbCommand cmd, string name, object? value, System.Data.DbType? dbType = null)
    {
        GuardTenantParameter(name, value);
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        if (dbType.HasValue)
        {
            parameter.DbType = dbType.Value;
        }

        cmd.Parameters.Add(parameter);
        return parameter;
    }
}
