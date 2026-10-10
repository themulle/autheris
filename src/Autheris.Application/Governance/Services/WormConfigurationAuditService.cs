namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
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
    private readonly byte[] _hmacSecretKey = Encoding.UTF8.GetBytes("Autheris_Worm_Config_Sealing_Key_2026_Strict");

    private string? _currentConfigHash;
    private long _currentPolicyEpoch;

    public WormConfigurationAuditService(
        IOptionsMonitor<GatewayOptions> gatewayOptions,
        ILogger<WormConfigurationAuditService> logger)
    {
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
