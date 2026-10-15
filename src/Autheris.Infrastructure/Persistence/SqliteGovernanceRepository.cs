using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Infrastructure.Persistence;

public partial class SqliteGovernanceRepository : IGovernanceRepository, Migrations.IGovernanceStoreInitializer, IDisposable
{
    private readonly SqliteGovernanceConnectionProvider _connections;
    internal SqliteGovernanceConnectionProvider Connections => _connections;

    private readonly IEpochValidationService _epochValidationService;

    /// <summary>
    /// The single write gate (WP-ARC-B-13): held by EVERY mutating operation, the audit channel batches, the synchronous Tier-A path, the
    /// flush and the chain verification. In-memory databases also serialize their readers behind it. It keeps the audit hash chain linear
    /// (the in-memory tail is advanced inside business transactions) and makes check-then-write sequences such as the four-eyes
    /// approval atomic.
    /// </summary>
    private readonly ReadinessGatedLock _writeGate;

    /// <summary>
    /// Identity of the connection of the operation that currently holds the write gate; an audit write inside a caller-managed
    /// transaction must run on it. Held as an identity token only (compared by reference, never used to execute a command), so the
    /// repository has no connection field that could be shared between operations.
    /// </summary>
    private object? _writeConnection;
    private long _metadataCacheGeneration;

    // Keys invalidated by the write operation that holds the gate; applied once its transaction has committed (scope disposal).
    private readonly List<string> _pendingMetadataInvalidations = [];

    /// <summary>Test seam: runs after a metadata read has loaded its snapshot and before the result is cached.</summary>
    internal Func<CancellationToken, Task>? AfterMetadataSnapshotForTesting { get; set; }

    private void QueueMetadataInvalidation(TableIdentifier table) => _pendingMetadataInvalidations.Add(table.ToString().ToLowerInvariant());

    /// <summary>Test seam: drops cached metadata after a test changed rows behind the repository.</summary>
    private readonly CatalogKeyDefinitionStore.KeyTableProbe _keyTables = new();

    /// <summary>Models a fresh repository instance for tests that drop the key tables after the probe was cached.</summary>
    internal void ResetKeyTableProbeForTesting() => _keyTables.Reset();

    internal void InvalidateMetadataCacheForTesting() => _metadataCache.Clear();

    private void ApplyMetadataInvalidations()
    {
        if (_pendingMetadataInvalidations.Count == 0)
        {
            return;
        }

        // The generation moves first so that readers with an older snapshot do not re-cache what is removed next.
        Interlocked.Increment(ref _metadataCacheGeneration);
        foreach (var key in _pendingMetadataInvalidations)
        {
            _metadataCache.TryRemove(key, out _);
        }

        _pendingMetadataInvalidations.Clear();
    }

    private volatile bool _ready;
    private void ThrowIfNotReady()
    {
        if (!_ready)
        {
            throw new Migrations.GovernanceStoreNotReadyException(nameof(SqliteGovernanceRepository));
        }
    }

    // The migration runner runs before the store is ready; it is the only caller that may enter the gate unguarded.
    internal Task LockUnguardedAsync(CancellationToken ct = default) => _writeGate.WaitUnguardedAsync(ct);
    internal void ReleaseLock() => _writeGate.Release();

    /// <summary>An operation's connection and, for writers (and in-memory readers), its hold on the write gate. Disposing returns both.</summary>
    private sealed class OperationScope(SqliteGovernanceRepository owner, SqliteConnection connection, bool holdsGate, bool isWrite, SqliteTransaction? snapshot) : IAsyncDisposable
    {
        public SqliteConnection Connection => connection;

        /// <summary>The deferred read transaction of a snapshot scope.</summary>
        public SqliteTransaction? Snapshot => snapshot;

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (snapshot != null)
                {
                    await snapshot.DisposeAsync().ConfigureAwait(false);
                }

                if (isWrite)
                {
                    owner._writeConnection = null;
                }

                await connection.DisposeAsync().ConfigureAwait(false);
                if (isWrite)
                {
                    owner.RollbackPendingAuditTransactions();
                    owner.ApplyMetadataInvalidations();
                }
            }
            finally
            {
                if (holdsGate)
                {
                    owner._writeGate.Release();
                }
            }
        }
    }

    /// <summary>Enters the write gate (readiness checked) and opens a connection for a mutating operation.</summary>
    private async Task<OperationScope> WriteScopeAsync(CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
            _writeConnection = connection;
            return new OperationScope(this, connection, holdsGate: true, isWrite: true, snapshot: null);
        }
        catch
        {
            _writeGate.Release();
            throw;
        }
    }

    /// <summary>
    /// Opens a connection for a read. File databases read concurrently with the writer (WAL); in-memory databases also hold the write gate
    /// (shared-cache table locks). <paramref name="snapshot"/> reads several statements from one consistent snapshot.
    /// </summary>
    private async Task<OperationScope> ReadScopeAsync(CancellationToken ct, bool snapshot = false)
    {
        ThrowIfNotReady();
        var holdsGate = _connections.IsMemory;
        if (holdsGate)
        {
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
            SqliteTransaction? transaction = null;
            if (snapshot)
            {
                try
                {
                    transaction = connection.BeginTransaction(deferred: true);
                }
                catch
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            return new OperationScope(this, connection, holdsGate, isWrite: false, snapshot: transaction);
        }
        catch
        {
            if (holdsGate)
            {
                _writeGate.Release();
            }

            throw;
        }
    }

    private async Task<T> WriteAsync<T>(Func<SqliteConnection, Task<T>> body, CancellationToken ct)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        return await body(scope.Connection).ConfigureAwait(false);
    }

    private async Task<T> ReadAsync<T>(Func<SqliteConnection, Task<T>> body, CancellationToken ct, bool snapshot = false)
    {
        await using var scope = await ReadScopeAsync(ct, snapshot).ConfigureAwait(false);
        return await body(scope.Connection).ConfigureAwait(false);
    }
    // WP-PF-06: bounded (a 10k-table catalog must not grow the cache without limit); a miss just reloads the metadata.
    private readonly Autheris.Application.Caching.BoundedCache<string, (TableMetadata? Metadata, long CachedAtTicks)> _metadataCache = new(20_000, comparer: StringComparer.OrdinalIgnoreCase);
    private static readonly long MetadataCacheTtlTicks = TimeSpan.FromSeconds(30).Ticks;
    private volatile bool _isAuditPipelineFaulted = false;
    public bool IsAuditPipelineFaulted => _isAuditPipelineFaulted || Volatile.Read(ref _auditChainViolation) != null;

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (!_ready)
        {
            return false;
        }

        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        await using var cmd = scope.Connection.CreateCommand();
        cmd.CommandText = "SELECT 1;";
        var res = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return res != null;
    }

    private readonly Channel<AuditLogEntry> _auditChannel;
    private readonly CancellationTokenSource _auditCts = new();
    private Task? _auditProcessorTask;
    private int _initialized;
    private bool _isDevOrTest;
    private bool _shouldSeed;
    private static readonly byte[] ProcessEphemeralAuditHmacKey = RandomNumberGenerator.GetBytes(32);
    private string _lastAuditHash = "GENESIS_0000000000000000000000000000000000000000000000000000000000000000";
    private long _lastAuditSeq;
    private readonly byte[] _auditHmacKey;
    private readonly byte[] _auditAnchorKey;
    private readonly IAuditChainAnchorStore _auditAnchorStore;
    private readonly ILogger<SqliteGovernanceRepository>? _logger;
    private string? _auditChainViolation;
    private readonly GatewayOptions? _options;
    private readonly ICallerContextProvider? _callerContextProvider;

    public SqliteGovernanceRepository(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions> options,
        Microsoft.Extensions.Hosting.IHostEnvironment environment,
        IKeyVaultSecretProvider secretProvider,
        IAuditChainAnchorStore auditAnchorStore,
        ICallerContextProvider callerContextProvider,
        IGatewayStatePaths statePaths,
        ILogger<SqliteGovernanceRepository>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(epochValidationService);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(secretProvider);
        ArgumentNullException.ThrowIfNull(auditAnchorStore);
        ArgumentNullException.ThrowIfNull(callerContextProvider);
        ArgumentNullException.ThrowIfNull(statePaths);

        _epochValidationService = epochValidationService;
        _writeGate = new ReadinessGatedLock(() => _ready, nameof(SqliteGovernanceRepository));
        _logger = logger;
        _options = options.Value;
        _callerContextProvider = callerContextProvider;
        var connStr = statePaths.ResolveGovernanceConnectionString(options.Value?.GovernanceDb?.ConnectionString);

        // No I/O and no DDL here: the provider opens connections lazily (directory, journal mode) and the schema is created and
        // evolved by the governance migration runner (WP-ARC-B-12).
        _connections = new SqliteGovernanceConnectionProvider(
            connStr,
            options.Value?.GovernanceDb?.JournalMode,
            logger,
            allowUnsafeJournalModes: string.Equals(environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase));

        // SEC R2-3: Bounded channel with backpressure to limit in-flight audit entries
        var channelOptions = new BoundedChannelOptions(Math.Max(1, options?.Value?.Audit?.QueryAuditChannelCapacity ?? 5_000))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        _auditChannel = Channel.CreateBounded<AuditLogEntry>(channelOptions);

        // SEC-04: Resolve or derive dedicated HMAC-SHA256 key for authentic tamper-evident audit logging (N-6)
        byte[]? key = null;
        var auditSecretRef = options?.Value?.GovernanceDb?.AuditHmacKeyVaultRef;
        if (secretProvider != null && !string.IsNullOrWhiteSpace(auditSecretRef))
        {
            try
            {
                key = secretProvider.GetSecretBytes(auditSecretRef);
            }
            catch (Exception)
            {
                bool isDev = string.Equals(environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);
                if (!isDev)
                {
                    throw new InvalidOperationException("Security critical: Failed to load AuditHmacKeyVaultRef from Key Vault in non-development environment.");
                }
            }
        }

        // R-DEP-1: the length is checked here, where the key is used, not by its reference name.
        var envName = environment.EnvironmentName;
        bool isDevOrTest = string.Equals(envName, "Development", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(envName, "Test", StringComparison.OrdinalIgnoreCase);
        if (key != null)
        {
            Autheris.Application.Security.SecretKeyRequirements.EnsureMinimumLength(key, "The audit HMAC key (AuditHmacKeyVaultRef)", isDevOrTest);
        }

        if (key == null && secretProvider != null && !string.IsNullOrWhiteSpace(options?.Value?.DataMasking?.HmacSecretKeyVaultRef))
        {
            byte[]? masterKey = null;
            try
            {
                masterKey = secretProvider.GetSecretBytes(options.Value.DataMasking.HmacSecretKeyVaultRef);
            }
            catch
            {
                // Fallback below
            }

            if (masterKey != null && masterKey.Length > 0)
            {
                // R-DEP-1: HKDF does not add entropy; a short master key yields a weak audit key.
                Autheris.Application.Security.SecretKeyRequirements.EnsureMinimumLength(masterKey, "The HMAC master key (HmacSecretKeyVaultRef)", isDevOrTest);
            }

            try
            {
                if (masterKey != null && masterKey.Length > 0)
                {
                    // HKDF key separation: ensure audit HMAC key is cryptographically isolated from column masking
                    key = System.Security.Cryptography.HKDF.DeriveKey(
                        System.Security.Cryptography.HashAlgorithmName.SHA256,
                        masterKey,
                        32,
                        info: "Autheris:AuditChain:v1"u8.ToArray());
                }
            }
            catch
            {
                // Fallback below
            }
        }

        var isMemory = _connections.IsMemory;

        if (!isDevOrTest)
        {
            if (isMemory)
            {
                throw new InvalidOperationException(
                    "Security critical: In-Memory SQLite governance database is strictly forbidden in non-development/production environments. A persistent database must be configured.");
            }

            if (key == null)
            {
                throw new InvalidOperationException(
                    "Security critical: Audit HMAC secret is missing or could not be resolved from Key Vault in a non-development environment. Tamper-evident audit logging cannot use default fallback keys.");
            }
        }

        _auditHmacKey = key ?? ProcessEphemeralAuditHmacKey;

        // SEC H-17: dedicated sub-key for signing the external audit chain end anchor.
        _auditAnchorKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, _auditHmacKey, 32, info: "Autheris:AuditChainAnchor:v1"u8.ToArray());

        // AU-01 & AU-03 are enforced where the anchor store is built (AuditChainAnchorStoreFactory.CreateForGovernance).
        // Defense in depth: an injected in-memory anchor store never protects a production chain.
        if (!isDevOrTest && auditAnchorStore is InMemoryAuditChainAnchorStore)
        {
            throw new InvalidOperationException(
                "CRITICAL AUDIT MISCONFIGURATION (AU-01/AU-03): In production environments, " +
                "a persistent audit anchor store (ChainAnchorPath, ChainAnchorWormDirectory, or KMS Signer) " +
                "is mandatory. In-memory anchor stores are strictly prohibited outside Development/Test.");
        }

        _auditAnchorStore = auditAnchorStore;
        _isDevOrTest = isDevOrTest;
        _shouldSeed = options?.Value?.GovernanceDb?.SeedDemoData ?? (isMemory && isDevOrTest);
    }

    /// <summary>
    /// Post-migration initialization: reads the audit chain tail, verifies the external chain anchor, seeds demo data (when
    /// configured) and starts the audit processor. Requires the schema to be migrated first; fails closed otherwise.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        try
        {
            // Initialization runs before the store is ready, so it enters the gate unguarded (like the migration runner).
            await _writeGate.WaitUnguardedAsync(ct).ConfigureAwait(false);
            try
            {
                await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);

                // SEC H-17: unsequenced rows are latched as a violation before the tail is read.
                LatchUnsequencedAuditRows(connection);

                // Startup reference only; it is cross-checked against the external signed anchor (SEC H-17)
                // in InitializeAuditChainAnchor and never silently re-synchronised from the DB afterwards.
                var (tailHash, tailSeq) = ReadAuditTail(connection, null);
                if (tailHash != null)
                {
                    _lastAuditHash = tailHash;
                    _lastAuditSeq = tailSeq;
                }

                InitializeAuditChainAnchor(_isDevOrTest);

                if (_shouldSeed)
                {
                    SeedInitialCatalog(connection);
                }
            }
            finally
            {
                _writeGate.Release();
            }

            // The store serves requests only from here on; a failed initialization leaves it closed.
            _ready = true;

            // AU-12: Background processor starts only after keys and stores are completely initialized
            _auditProcessorTask = Task.Run(ProcessAuditChannelAsync, CancellationToken.None);
        }
        catch
        {
            Volatile.Write(ref _initialized, 0);
            throw;
        }
    }


    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _auditChannel.Writer.TryComplete();
        try
        {
            _auditCts.Cancel();
        }
        catch (ObjectDisposedException) { }

        try
        {
            _auditProcessorTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error while draining audit channel during disposal.");
        }

        try
        {
            _auditCts.Dispose();
        }
        catch (ObjectDisposedException) { }

        try
        {
            _connections.Dispose();
        }
        catch { }

        try
        {
            _writeGate.Dispose();
        }
        catch { }
    }
}
