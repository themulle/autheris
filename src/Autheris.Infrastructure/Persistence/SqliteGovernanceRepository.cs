using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        ILogger<SqliteGovernanceRepository>? logger = null)
    {
        _epochValidationService = epochValidationService;
        _logger = logger;
        _options = options?.Value;
        var connStr = options?.Value?.GovernanceDb?.ConnectionString ?? $"Data Source=governance_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _connection = new SqliteConnection(connStr);
        _connection.Open();

        InitializeDatabase();

        // SEC-04: Resolve or derive dedicated HMAC-SHA256 key for authentic tamper-evident audit logging (N-6)
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

        var envName = environment?.EnvironmentName ??
                      Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
                      Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        bool isDevOrTest = string.IsNullOrEmpty(envName) ||
                           string.Equals(envName, "Development", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(envName, "Testing", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(envName, "Test", StringComparison.OrdinalIgnoreCase);

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
        _auditAnchorStore = auditAnchorStore ?? CreateDefaultAuditAnchorStore(connStr, isMemory, options?.Value?.Audit?.ChainAnchorPath);
        InitializeAuditChainAnchor(isDevOrTest);

        bool shouldSeed = options?.Value?.GovernanceDb?.SeedDemoData ?? (isMemory && isDevOrTest);
        if (shouldSeed)
        {
            SeedInitialCatalog();
        }
    }


    public void Dispose()
    {
        _connection.Dispose();
        _lock.Dispose();
    }
}
