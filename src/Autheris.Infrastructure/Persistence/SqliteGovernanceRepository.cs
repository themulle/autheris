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

public partial class SqliteGovernanceRepository : IGovernanceRepository, IDisposable
{
    private readonly SqliteConnection _connection;
    internal SqliteConnection Connection => _connection;
    private readonly IEpochValidationService _epochValidationService;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, (TableMetadata? Metadata, long CachedAtTicks)> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly long MetadataCacheTtlTicks = TimeSpan.FromSeconds(30).Ticks;
    private volatile bool _isAuditPipelineFaulted = false;
    public bool IsAuditPipelineFaulted => _isAuditPipelineFaulted;

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
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
    private readonly Task _auditProcessorTask;
    private string _lastAuditHash = "GENESIS_0000000000000000000000000000000000000000000000000000000000000000";
    private long _lastAuditSeq;
    private readonly byte[] _auditHmacKey;
    private readonly byte[] _auditAnchorKey;
    private readonly IAuditChainAnchorStore _auditAnchorStore;
    private readonly ILogger<SqliteGovernanceRepository>? _logger;
    private string? _auditChainViolation;
    private readonly GatewayOptions? _options;

    public SqliteGovernanceRepository(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions>? options = null,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null,
        IKeyVaultSecretProvider? secretProvider = null,
        IAuditChainAnchorStore? auditAnchorStore = null,
        ILogger<SqliteGovernanceRepository>? logger = null,
        IAuditAnchorSigner? auditAnchorSigner = null)
    {
        _epochValidationService = epochValidationService;
        _logger = logger;
        _options = options?.Value;
        var connStr = options?.Value?.GovernanceDb?.ConnectionString ?? $"Data Source=governance_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        EnsureSqliteDirectoryExists(connStr);
        _connection = new SqliteConnection(connStr);
        _connection.Open();

        InitializeDatabase();

        // SEC R2-3: Bounded channel with backpressure to limit in-flight audit entries
        var channelOptions = new BoundedChannelOptions(Math.Max(1, options?.Value?.Audit?.QueryAuditChannelCapacity ?? 5_000))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        _auditChannel = Channel.CreateBounded<AuditLogEntry>(channelOptions);
        _auditProcessorTask = Task.Run(ProcessAuditChannelAsync);

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
        bool isDevOrTest = string.IsNullOrEmpty(envName) ||
                           string.Equals(envName, "Development", StringComparison.OrdinalIgnoreCase);
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

        if (key == null)
        {
            _auditHmacKey = "AutherisAuditLogHmacTamperEvidenceSecret2026!"u8.ToArray();
        }
        else
        {
            _auditHmacKey = key;
        }

        // SEC H-17: dedicated sub-key for signing the external audit chain end anchor.
        _auditAnchorKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, _auditHmacKey, 32, info: "Autheris:AuditChainAnchor:v1"u8.ToArray());
        _auditAnchorStore = auditAnchorStore ?? AuditChainAnchorStoreFactory.Create(
            options?.Value?.Audit,
            CreateDefaultAuditAnchorStore(connStr, isMemory, options?.Value?.Audit?.ChainAnchorPath),
            secretProvider,
            auditAnchorSigner,
            _logger);
        InitializeAuditChainAnchor(isDevOrTest);

        bool shouldSeed = options?.Value?.GovernanceDb?.SeedDemoData ?? (isMemory && isDevOrTest);
        if (shouldSeed)
        {
            SeedInitialCatalog();
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
            _auditProcessorTask.GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore cancellation or drain exceptions during disposal
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
