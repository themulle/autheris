namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Implements WORM configuration auditing with canonical SHA-256 hashing and timing-safe signature checks.
/// </summary>
public sealed class WormConfigurationAuditService : IWormConfigurationAuditService
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    private readonly IOptionsMonitor<GatewayOptions> _gatewayOptions;
    private readonly ILogger<WormConfigurationAuditService> _logger;
    private readonly byte[] _hmacSecretKey;

    // SR-D-02: development-only sealing key; any other environment must derive the key from the audit key vault secret.
    private static readonly byte[] DevelopmentSealingKey = Encoding.UTF8.GetBytes("Autheris_Worm_Config_Sealing_Key_Development_Only");
    private static readonly byte[] SealingKeyInfo = Encoding.UTF8.GetBytes("Autheris:WormConfigSeal:v1");

    private string? _currentConfigHash;
    private long _currentPolicyEpoch;

    public WormConfigurationAuditService(
        IOptionsMonitor<GatewayOptions> gatewayOptions,
        ILogger<WormConfigurationAuditService> logger,
        IKeyVaultSecretProvider? secretProvider = null,
        IHostEnvironment? environment = null)
    {
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hmacSecretKey = ResolveSealingKey(_gatewayOptions.CurrentValue, secretProvider, environment);
        // The baseline hash is intentionally NOT seeded here: the first audit snapshot (startup bootstrap)
        // must mint epoch 1 chained to the all-zero genesis hash. Seeding would suppress that WORM record.
    }

    /// <summary>
    /// SR-D-02: HKDF(audit key, info "Autheris:WormConfigSeal:v1"). The audit key is AuditHmacKeyVaultRef, falling back to
    /// DataMasking.HmacSecretKeyVaultRef like the audit chain. Fails closed outside Development/Test. Seals minted with
    /// the former hard-coded key do not verify against the derived key.
    /// </summary>
    private static byte[] ResolveSealingKey(GatewayOptions options, IKeyVaultSecretProvider? secretProvider, IHostEnvironment? environment)
    {
        var envName = environment?.EnvironmentName ??
                      Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
                      Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var isDevOrTest = string.Equals(envName, "Development", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(envName, "Test", StringComparison.OrdinalIgnoreCase);

        byte[]? master = null;
        if (secretProvider != null)
        {
            var ref1 = options.GovernanceDb?.AuditHmacKeyVaultRef;
            var ref2 = options.DataMasking?.HmacSecretKeyVaultRef;
            var secretRef = !string.IsNullOrWhiteSpace(ref1) ? ref1 : ref2;
            if (!string.IsNullOrWhiteSpace(secretRef))
            {
                try
                {
                    master = secretProvider.GetSecretBytes(secretRef);
                }
                catch (Exception) when (isDevOrTest)
                {
                    // Development/Test: fall back to the development key below.
                }
            }
        }

        if (master != null && master.Length > 0)
        {
            SecretKeyRequirements.EnsureMinimumLength(master, "The WORM configuration sealing master key (AuditHmacKeyVaultRef)", isDevOrTest);
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: SealingKeyInfo);
        }

        if (!isDevOrTest)
        {
            throw new InvalidOperationException(
                "Security critical: No AuditHmacKeyVaultRef or HmacSecretKeyVaultRef secret available in a non-development environment. Cannot seal WORM configuration records.");
        }

        return DevelopmentSealingKey;
    }

    public string? CurrentConfigHash => _currentConfigHash;

    public string ComputeCanonicalHash(ClassificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var canonicalJson = JsonSerializer.Serialize(options, CanonicalJsonOptions);
        var bytes = Encoding.UTF8.GetBytes(canonicalJson);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public ValueTask<WormConfigurationRecord?> AuditConfigurationSnapshotAsync(
        string trigger,
        string actorSid = "SYSTEM",
        CancellationToken ct = default)
    {
        var options = _gatewayOptions.CurrentValue.Classification ?? new ClassificationOptions();
        var newHash = ComputeCanonicalHash(options);

        if (_currentConfigHash != null && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(_currentConfigHash),
                Encoding.UTF8.GetBytes(newHash)))
        {
            // Unchanged: No new WORM block needed
            return ValueTask.FromResult<WormConfigurationRecord?>(null);
        }

        var previousHash = _currentConfigHash ?? "0000000000000000000000000000000000000000000000000000000000000000";
        var epoch = Interlocked.Increment(ref _currentPolicyEpoch);
        _currentConfigHash = newHash;

        var canonicalJson = JsonSerializer.Serialize(options, CanonicalJsonOptions);
        var signaturePayload = $"{epoch}:{trigger}:{actorSid}:{newHash}:{previousHash}";
        var signature = ComputeHmacSha256(signaturePayload);

        var record = new WormConfigurationRecord(
            ConfigAuditId: Guid.NewGuid(),
            PolicyEpoch: epoch,
            Trigger: trigger,
            ActorSid: actorSid,
            Sha256ConfigHash: newHash,
            PreviousConfigHash: previousHash,
            ModifiedSections: new[] { "Gateway.Classification" },
            CanonicalConfigJson: canonicalJson,
            WormSignature: signature,
            TimestampUtc: DateTimeOffset.UtcNow);

        _logger.LogInformation("WORM Configuration Record minted: Epoch {Epoch}, Trigger {Trigger}, Hash {Hash}",
            epoch, trigger, newHash);

        return ValueTask.FromResult<WormConfigurationRecord?>(record);
    }

    private string ComputeHmacSha256(string data)
    {
        using var hmac = new HMACSHA256(_hmacSecretKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
