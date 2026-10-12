using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;

namespace Autheris.Infrastructure.Persistence;

public partial class SqlServerGovernanceRepository : IGovernanceRepository, IAuditChainExportSource, IDisposable, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly IEpochValidationService _epochValidationService;
    private readonly ConcurrentDictionary<string, (TableMetadata? Metadata, long CachedAtTicks)> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly long MetadataCacheTtlTicks = TimeSpan.FromSeconds(30).Ticks;
    private readonly SemaphoreSlim _auditLock = new(1, 1);
    private volatile bool _isAuditPipelineFaulted = false;
    public bool IsAuditPipelineFaulted => _isAuditPipelineFaulted;

    private readonly Channel<AuditLogEntry> _auditChannel;
    private readonly CancellationTokenSource _auditCts = new();
    private readonly Task _auditProcessorTask;
    private string _lastAuditHash = "GENESIS_0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly byte[] ProcessEphemeralAuditHmacKey = RandomNumberGenerator.GetBytes(32);
    private long _lastAuditSeq;
    private readonly byte[] _auditHmacKey;
    private readonly byte[] _auditAnchorKey;
    private readonly IAuditChainAnchorStore? _auditAnchorStore;
    private readonly bool _isDevOrTest;
    private readonly string? _migrationConnectionString;
    private readonly ILogger<SqlServerGovernanceRepository>? _logger;
    private string? _auditChainViolation;
    private readonly GatewayOptions? _options;

    public SqlServerGovernanceRepository(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions> options,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null,
        IKeyVaultSecretProvider? secretProvider = null,
        IAuditChainAnchorStore? auditAnchorStore = null,
        ILogger<SqlServerGovernanceRepository>? logger = null,
        IAuditAnchorSigner? auditAnchorSigner = null)
    {
        ArgumentNullException.ThrowIfNull(epochValidationService);
        ArgumentNullException.ThrowIfNull(options);

        _epochValidationService = epochValidationService;
        _logger = logger;
        _options = options.Value;

        var connStr = options?.Value?.GovernanceDb?.ConnectionString;
        var transportEnv = environment?.EnvironmentName ??
                           Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
                           Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        bool isDevTransport = string.Equals(transportEnv, "Development", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(transportEnv, "Test", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(connStr))
        {
            // Review PG-7: no default credentials outside Development/Test.
            if (!isDevTransport)
            {
                throw new InvalidOperationException("Security critical: GovernanceDb:ConnectionString is not configured for the SQL Server provider.");
            }

            connStr = "Server=localhost;Database=autheris_governance;User Id=sa;Password=Autheris_Dev_Passw0rd!;TrustServerCertificate=true";
        }

        // Review PG-7: the governance database carries consent, audit and secrets metadata; require TLS outside Development/Test.
        if (!isDevTransport && options?.Value?.IsInsecureTransportAllowed != true)
        {
            foreach (var (name, candidate) in new[] { ("ConnectionString", connStr), ("MigrationConnectionString", options?.Value?.GovernanceDb?.MigrationConnectionString) })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                // DEP-7: Encrypt=Mandatory/Strict and TrustServerCertificate=false (server certificate verified).
                if (ConnectionTlsPolicy.Validate("sqlserver", candidate) is { } tlsError)
                {
                    throw new InvalidOperationException($"Security critical: GovernanceDb:{name}: {tlsError}");
                }
            }
        }

        _migrationConnectionString = options?.Value?.GovernanceDb?.MigrationConnectionString;

        var builder = new SqlConnectionStringBuilder(connStr);
        if (options?.Value?.GovernanceDb?.CommandTimeoutSeconds > 0)
        {
            builder.CommandTimeout = options.Value.GovernanceDb.CommandTimeoutSeconds;
        }

        _connectionString = builder.ConnectionString;

        // Bounded channel with backpressure to limit in-flight audit entries
        var channelOptions = new BoundedChannelOptions(Math.Max(1, options?.Value?.Audit?.QueryAuditChannelCapacity ?? 5_000))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        _auditChannel = Channel.CreateBounded<AuditLogEntry>(channelOptions);

        // SEC-04: Resolve or derive dedicated HMAC-SHA256 key for authentic tamper-evident audit logging
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
                var env = environment?.EnvironmentName ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
                bool isDev = string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase);
                if (!isDev)
                {
                    throw new InvalidOperationException("Security critical: Failed to load AuditHmacKeyVaultRef from Key Vault in non-development environment.");
                }
            }
        }

        // R-DEP-1: the length is checked here, where the key is used, not by its reference name.
        var envName = environment?.EnvironmentName ??
                      Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
                      Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
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
                    key = HKDF.DeriveKey(
                        HashAlgorithmName.SHA256,
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


        if (key == null || key.Length == 0)
        {
            if (!isDevOrTest)
            {
                throw new InvalidOperationException(
                    "Security critical: No AuditHmacKeyVaultRef or HmacSecretKeyVaultRef configured in non-development environment. Cannot ensure audit log integrity.");
            }

            // AU-03: Process-ephemeral random key for Dev/Test - static fallback removed
            key = ProcessEphemeralAuditHmacKey;
        }

        _auditHmacKey = key;

        _auditAnchorKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            _auditHmacKey,
            32,
            info: "Autheris:AuditAnchor:v1"u8.ToArray());

        _isDevOrTest = isDevOrTest;

        // AU-01 & AU-03: Startabbruch (Fail-Closed) außerhalb von Dev/Test ohne persistenten Anker-Pfad oder KMS-Signer.
        if (!isDevOrTest && auditAnchorStore == null)
        {
            if (string.IsNullOrWhiteSpace(options?.Value?.Audit?.ChainAnchorPath) &&
                string.IsNullOrWhiteSpace(options?.Value?.Audit?.ChainAnchorWormDirectory) &&
                string.IsNullOrWhiteSpace(options?.Value?.Audit?.ChainAnchorSignerKeyVaultRef) &&
                auditAnchorSigner == null)
            {
                throw new InvalidOperationException(
                    "CRITICAL AUDIT MISCONFIGURATION (AU-01/AU-03): In production environments, " +
                    "a persistent audit anchor store (ChainAnchorPath, ChainAnchorWormDirectory, or KMS Signer) " +
                    "is mandatory. In-memory anchor stores are strictly prohibited outside Development/Test.");
            }
        }

        // Review PG-2: anchor store from Audit:ChainAnchorPath (use a shared, separately protected location when running
        // several replicas); in-memory only in Development/Test or when nothing is configured (a warning is logged).
        _auditAnchorStore = auditAnchorStore ?? AuditChainAnchorStoreFactory.Create(
            options?.Value?.Audit,
            !string.IsNullOrWhiteSpace(options?.Value?.Audit?.ChainAnchorPath)
                ? new FileAuditChainAnchorStore(options.Value.Audit.ChainAnchorPath)
                : new InMemoryAuditChainAnchorStore(),
            secretProvider,
            auditAnchorSigner,
            _logger);

        bool shouldSeed = options?.Value?.GovernanceDb?.SeedDemoData ?? isDevOrTest;
        InitializeDatabaseSafely(shouldSeed);

        // AU-12: Background processor starts only after keys and stores are completely initialized
        _auditProcessorTask = Task.Run(ProcessAuditChannelAsync);
    }

    private void InitializeDatabaseSafely(bool shouldSeed)
    {
        try
        {
            InitializeDatabase();
            InitializeAuditChainAnchor();
            if (shouldSeed)
            {
                SeedInitialCatalog();
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _isInitialized, 0);
            _logger?.LogWarning(ex, "Could not eagerly initialize SQL Server governance database during startup (e.g. host unreachable or test environment). Database will be initialized on first connection or health check.");
        }
    }

    private SqlConnection OpenConnection()
    {
        var conn = new SqlConnection(_connectionString);
        try
        {
            conn.Open();
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            if (Volatile.Read(ref _isInitialized) == 0)
            {
                InitializeDatabaseSafely(_options?.GovernanceDb?.SeedDemoData ?? false);
            }
            await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1;";
            var res = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return res != null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SQL Server Governance DB Ping failed.");
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

        try
        {
            _auditCts.Cancel();
        }
        catch (ObjectDisposedException) { }

        try
        {
            await _auditProcessorTask.ConfigureAwait(false);
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

        _auditLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
