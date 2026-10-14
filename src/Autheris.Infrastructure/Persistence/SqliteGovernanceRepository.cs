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
    private readonly SqliteConnection _connection;
    internal SqliteConnection Connection => _connection;
    private readonly IEpochValidationService _epochValidationService;
    private readonly ReadinessGatedLock _lock;
    private volatile bool _ready;
    private void ThrowIfNotReady()
    {
        if (!_ready)
        {
            throw new Migrations.GovernanceStoreNotReadyException(nameof(SqliteGovernanceRepository));
        }
    }

    // The migration runner runs before the store is ready; it is the only caller that may enter the gate unguarded.
    internal Task LockAsync(CancellationToken ct = default) => _lock.WaitUnguardedAsync(ct);
    internal void ReleaseLock() => _lock.Release();
    private readonly ConcurrentDictionary<string, (TableMetadata? Metadata, long CachedAtTicks)> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly long MetadataCacheTtlTicks = TimeSpan.FromSeconds(30).Ticks;
    private volatile bool _isAuditPipelineFaulted = false;
    public bool IsAuditPipelineFaulted => _isAuditPipelineFaulted || Volatile.Read(ref _auditChainViolation) != null;

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (!_ready)
        {
            return false;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT 1;";
            var res = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return res != null;
        }
        finally
        {
            _lock.Release();
        }
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
        _lock = new ReadinessGatedLock(() => _ready, nameof(SqliteGovernanceRepository));
        _logger = logger;
        _options = options.Value;
        _callerContextProvider = callerContextProvider;
        var connStr = statePaths.ResolveGovernanceConnectionString(options.Value?.GovernanceDb?.ConnectionString);
        EnsureSqliteDirectoryExists(connStr);
        _connection = new SqliteConnection(connStr);
        _connection.Open();

        // No DDL here: the schema is created and evolved by the governance migration runner (WP-ARC-B-12).
        ConfigureConnection();

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

        bool isMemory;
        try
        {
            var csBuilder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connStr);
            isMemory = csBuilder.DataSource == ":memory:" || csBuilder.Mode == Microsoft.Data.Sqlite.SqliteOpenMode.Memory;
        }
        catch
        {
            isMemory = false;
        }


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
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return Task.CompletedTask;
        }

        try
        {
            // SEC H-17: unsequenced rows are latched as a violation before the tail is read.
            LatchUnsequencedAuditRows();

            // Startup reference only; it is cross-checked against the external signed anchor (SEC H-17)
            // in InitializeAuditChainAnchor and never silently re-synchronised from the DB afterwards.
            var (tailHash, tailSeq) = ReadAuditTail(null);
            if (tailHash != null)
            {
                _lastAuditHash = tailHash;
                _lastAuditSeq = tailSeq;
            }

            InitializeAuditChainAnchor(_isDevOrTest);

            if (_shouldSeed)
            {
                SeedInitialCatalog();
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

        return Task.CompletedTask;
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
            _connection.Dispose();
        }
        catch { }

        try
        {
            _lock.Dispose();
        }
        catch { }
    }

    private static void EnsureSqliteDirectoryExists(string connectionString)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder(connectionString);
            if (!string.IsNullOrWhiteSpace(builder.DataSource) &&
                !builder.DataSource.StartsWith(":memory:", StringComparison.OrdinalIgnoreCase) &&
                builder.Mode != SqliteOpenMode.Memory)
            {
                var dir = Path.GetDirectoryName(builder.DataSource);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
        }
        catch
        {
            // Ignore parse errors; SqliteConnection will validate
        }
    }
}
