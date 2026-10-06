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
using Npgsql;

namespace Autheris.Infrastructure.Persistence;

public partial class PostgreSqlGovernanceRepository : IGovernanceRepository, IAuditChainExportSource, IDisposable, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
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
    private long _lastAuditSeq;
    private readonly byte[] _auditHmacKey;
    private readonly byte[] _auditAnchorKey;
    private readonly IAuditChainAnchorStore? _auditAnchorStore;
    private readonly bool _isDevOrTest;
    private readonly string? _migrationConnectionString;
    private readonly ILogger<PostgreSqlGovernanceRepository>? _logger;
    private string? _auditChainViolation;
    private readonly GatewayOptions? _options;

    public PostgreSqlGovernanceRepository(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions> options,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null,
        IKeyVaultSecretProvider? secretProvider = null,
        IAuditChainAnchorStore? auditAnchorStore = null,
        ILogger<PostgreSqlGovernanceRepository>? logger = null)
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
        bool isDevTransport = string.IsNullOrEmpty(transportEnv) ||
                              string.Equals(transportEnv, "Development", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(transportEnv, "Testing", StringComparison.OrdinalIgnoreCase);
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
                var csb = new NpgsqlConnectionStringBuilder(candidate);
                if (csb.SslMode is not (SslMode.Require or SslMode.VerifyCA or SslMode.VerifyFull))
                {
                    throw new InvalidOperationException(
                        $"Security critical: GovernanceDb:{name} must use 'SSL Mode=Require', 'VerifyCA' or 'VerifyFull' outside Development.");
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

        // Bounded channel with backpressure to limit in-flight audit entries
        var channelOptions = new BoundedChannelOptions(5_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        _auditChannel = Channel.CreateBounded<AuditLogEntry>(channelOptions);
        _auditProcessorTask = Task.Run(ProcessAuditChannelAsync);

        // SEC-04: Resolve or derive dedicated HMAC-SHA256 key for authentic tamper-evident audit logging
        byte[]? key = null;
        var auditSecretRef = options?.Value?.GovernanceDb?.AuditHmacKeyVaultRef;
        if (secretProvider != null && !string.IsNullOrWhiteSpace(auditSecretRef))
        {
            try
            {
                key = secretProvider.GetSecretBytes(auditSecretRef);
            }
            catch (Exception ex)
            {
                var env = environment?.EnvironmentName ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
                bool isDev = string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(env, "Testing", StringComparison.OrdinalIgnoreCase);
                if (!isDev)
                {
                    throw new InvalidOperationException($"Security critical: Failed to load AuditHmacKeyVaultRef '{auditSecretRef}' from Key Vault in non-development environment.", ex);
                }
            }
        }

        if (key == null && secretProvider != null && !string.IsNullOrWhiteSpace(options?.Value?.DataMasking?.HmacSecretKeyVaultRef))
        {
            try
            {
                var masterKey = secretProvider.GetSecretBytes(options.Value.DataMasking.HmacSecretKeyVaultRef);
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

        var envName = environment?.EnvironmentName ??
                      Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
                      Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        bool isDevOrTest = string.IsNullOrEmpty(envName) ||
                           string.Equals(envName, "Development", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(envName, "Testing", StringComparison.OrdinalIgnoreCase);

        if (key == null || key.Length == 0)
        {
            if (!isDevOrTest && options?.Value?.IsInsecureTransportAllowed != true)
            {
                throw new InvalidOperationException(
                    "Security critical: No AuditHmacKeyVaultRef or HmacSecretKeyVaultRef configured in non-development environment. Cannot ensure audit log integrity.");
            }

            key = SHA256.HashData(Encoding.UTF8.GetBytes("autheris-dev-ephemeral-audit-hmac-salt-secure-fallback"));
        }

        _auditHmacKey = key;

        _auditAnchorKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            _auditHmacKey,
            32,
            info: "Autheris:AuditAnchor:v1"u8.ToArray());

        _isDevOrTest = isDevOrTest;

        // Review PG-2: anchor store from Audit:ChainAnchorPath (use a shared, separately protected location when running
        // several replicas); in-memory only in Development/Test or when nothing is configured (a warning is logged).
        _auditAnchorStore = auditAnchorStore ?? (!string.IsNullOrWhiteSpace(options?.Value?.Audit?.ChainAnchorPath)
            ? new FileAuditChainAnchorStore(options.Value.Audit.ChainAnchorPath)
            : new InMemoryAuditChainAnchorStore());

        bool shouldSeed = options?.Value?.GovernanceDb?.SeedDemoData ?? isDevOrTest;
        InitializeDatabaseSafely(shouldSeed);
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
            _logger?.LogWarning(ex, "Could not eagerly initialize PostgreSQL governance database during startup (e.g. host unreachable or test environment). Database will be initialized on first connection or health check.");
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
        await _dataSource.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
