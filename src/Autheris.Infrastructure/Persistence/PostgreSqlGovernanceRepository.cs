using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Autheris.Infrastructure.Persistence;

public partial class PostgreSqlGovernanceRepository : IGovernanceRepository, IConsentDescriptionRepository, Migrations.IGovernanceStoreInitializer, IAuditChainExportSource, IAuditPurgeKeyProvider, IDisposable, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly GovernanceRepository _governanceRepository;
    private volatile bool _ready;

    private void ThrowIfNotReady()
    {
        if (!_ready)
        {
            throw new Migrations.GovernanceStoreNotReadyException(nameof(PostgreSqlGovernanceRepository));
        }
    }

    // Every request-time connection goes through the guard; only initialization code opens connections directly.
    private ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        ThrowIfNotReady();
        return _dataSource.OpenConnectionAsync(ct);
    }

    private NpgsqlConnection OpenConnection()
    {
        ThrowIfNotReady();
        return _dataSource.OpenConnection();
    }
    private readonly IEpochValidationService _epochValidationService;
    // bounded (a 10k-table catalog must not grow the cache without limit); a miss just reloads the metadata.
    private Autheris.Application.State.Caching.BoundedCache<string, (TableMetadata? Metadata, long CachedAtTicks, long Epoch)> _metadataCache => _governanceRepository.MetadataCache;

    /// <summary>Test seam: drops cached metadata after a test changed rows behind the repository.</summary>

    internal void InvalidateMetadataCacheForTesting() => _metadataCache.Clear();

    /// <summary>Test seam: a positive key-table probe is cached per instance; this models a fresh instance.</summary>
    internal void ResetKeyTableProbeForTesting() => _governanceRepository.ResetKeyTableProbeForTesting();

    /// <summary>Test seam: runs after a metadata read loaded its snapshot and before the result is cached.</summary>
    internal Func<CancellationToken, Task>? AfterMetadataSnapshotForTesting
    {
        get => _governanceRepository.AfterMetadataSnapshotForTesting;
        set => _governanceRepository.AfterMetadataSnapshotForTesting = value;
    }
    public bool IsAuditPipelineFaulted => _governanceRepository.IsAuditPipelineFaulted;
    private int _initialized;
    private readonly bool _shouldSeed;
    private readonly string? _migrationConnectionString;
    internal NpgsqlDataSource DataSource => _dataSource;
    internal string? MigrationConnectionString => _migrationConnectionString;
    private readonly ILogger<PostgreSqlGovernanceRepository>? _logger;

    private readonly Autheris.Application.Identity.Attributes.IConsentMaterializationGuard? _materializationGuard;

    public PostgreSqlGovernanceRepository(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions> options,
        Microsoft.Extensions.Hosting.IHostEnvironment environment,
        IKeyVaultSecretProvider secretProvider,
        IAuditChainAnchorStore auditAnchorStore,
        ICallerContextProvider callerContextProvider,
        ILogger<PostgreSqlGovernanceRepository>? logger = null,
        IEventBus? eventBus = null,
        Autheris.Application.Identity.Attributes.IConsentMaterializationGuard? materializationGuard = null)
    {
        _materializationGuard = materializationGuard;
        ArgumentNullException.ThrowIfNull(epochValidationService);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(secretProvider);
        ArgumentNullException.ThrowIfNull(auditAnchorStore);
        ArgumentNullException.ThrowIfNull(callerContextProvider);

        _epochValidationService = epochValidationService;
        _logger = logger;

        var connStr = options?.Value?.GovernanceDb?.ConnectionString;
        var transportEnv = environment.EnvironmentName;
        bool isDevTransport = string.Equals(transportEnv, "Development", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(transportEnv, "Test", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(connStr))
        {
            // Review PG-7: no default credentials outside Development/Test.
            if (!isDevTransport)
            {
                throw new InvalidOperationException("Security critical: GovernanceDb:ConnectionString is not configured for the PostgreSQL provider.");
            }

            connStr = "Host=localhost;Database=autheris_governance;Username=postgres;Password=postgres";
        }

        // Review PG-7: the governance database carries consent, audit and secrets metadata; require TLS outside Development/Test.
        if (!isDevTransport && options?.Value?.IsInsecureTransportAllowed != true)
        {
            foreach (var (name, candidate) in new[] { ("ConnectionString", connStr), ("MigrationConnectionString", options?.Value?.GovernanceDb?.MigrationConnectionString) })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                // DEP-7: 'Require' encrypts without verifying the server certificate (MITM) -> VerifyCA/VerifyFull only.
                if (ConnectionTlsPolicy.Validate("postgresql", candidate) is { } tlsError)
                {
                    throw new InvalidOperationException($"Security critical: GovernanceDb:{name}: {tlsError}");
                }
            }
        }

        _migrationConnectionString = options?.Value?.GovernanceDb?.MigrationConnectionString;

        var builder = new NpgsqlDataSourceBuilder(connStr);
        if (options?.Value?.GovernanceDb?.CommandTimeoutSeconds > 0)
        {
            builder.ConnectionStringBuilder.CommandTimeout = options.Value.GovernanceDb.CommandTimeoutSeconds;
        }

        _dataSource = builder.Build();

        _governanceRepository = new GovernanceRepository(
            new Dialects.PostgreSqlGovernanceDialect(_dataSource),
            epochValidationService,
            options,
            logger: logger,
            materializationGuard: materializationGuard,
            auditRecorder: null,
            onTransactionCommitted: null,
            onTransactionRolledBack: null,
            eventBus: eventBus,
            auditAnchorStore: auditAnchorStore,
            secretProvider: secretProvider,
            environment: environment,
            callerContextProvider: callerContextProvider);

        var envName = environment.EnvironmentName;
        bool isDevOrTest = string.Equals(envName, "Development", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(envName, "Test", StringComparison.OrdinalIgnoreCase);

        // No DDL and no database I/O here: the schema is created by the governance migration runner.
        _shouldSeed = options?.Value?.GovernanceDb?.SeedDemoData ?? isDevOrTest;
    }

    /// <summary>
    /// Post-migration initialization: reads the audit chain tail, verifies the external chain anchor, seeds demo data (when
    /// configured) and starts the audit processor. Requires the schema to be migrated first; fails closed otherwise.
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return Task.CompletedTask;
        }

        try
        {
            _governanceRepository.InitializeAudit();
            if (_shouldSeed)
            {
                _governanceRepository.SeedInitialCatalog();
            }

            // The store serves requests only from here on; a failed initialization leaves it closed.
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
            await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1;";
            var res = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return res != null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "PostgreSQL Governance DB Ping failed.");
            return false;
        }
    }

    private int _isDisposed;

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1) return;

        await _governanceRepository.DisposeAsync().ConfigureAwait(false);
        await _dataSource.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
