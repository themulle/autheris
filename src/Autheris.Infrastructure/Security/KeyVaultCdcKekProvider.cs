namespace Autheris.Infrastructure.Security;

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Resolves the CDC stream key-encryption keys through <see cref="IKeyVaultSecretProvider"/> (PLAN-CDC-99 D-A8, SEC-CDC99-14).
/// Each KEK secret must be at least 32 bytes; the key is <c>HKDF-SHA256(secret, info "autheris-cdc-kek-v1")</c>, cached per process.
/// A secret that equals the webhook KEK or the masking HMAC secret reference is refused (key separation). Only in Development, and
/// only when no KEK is configured, an ephemeral random KEK is generated (the stream is unreadable after a restart and fails closed).
/// Key bytes and secret names are never logged.
/// </summary>
public sealed class KeyVaultCdcKekProvider : ICdcKeyEncryptionKeyProvider
{
    public const string EphemeralKekId = "ephemeral-dev";
    public const string WebhookKekSecretRef = "cdc-webhook-kek";
    private const int MinSecretBytes = 32;
    private static readonly byte[] HkdfInfo = Encoding.ASCII.GetBytes("autheris-cdc-kek-v1");

    private readonly IKeyVaultSecretProvider _secrets;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<KeyVaultCdcKekProvider> _logger;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private readonly object _ephemeralGate = new();
    private byte[]? _ephemeral;

    public KeyVaultCdcKekProvider(
        IKeyVaultSecretProvider secrets, IOptionsMonitor<GatewayOptions> options, IHostEnvironment environment, ILogger<KeyVaultCdcKekProvider> logger)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string? ActiveKekId
    {
        get
        {
            var enc = _options.CurrentValue.CdcStream.Encryption;
            if (!string.IsNullOrWhiteSpace(enc.ActiveKekId))
            {
                return enc.ActiveKekId;
            }

            return enc.Keks.Count == 0 && _environment.IsDevelopment() ? EphemeralKekId : null;
        }
    }

    public bool TryGetKek(string kekId, out ReadOnlyMemory<byte> key)
    {
        key = default;
        if (string.IsNullOrEmpty(kekId))
        {
            return false;
        }

        var enc = _options.CurrentValue.CdcStream.Encryption;
        if (enc.Keks.Count == 0 && _environment.IsDevelopment() && string.Equals(kekId, EphemeralKekId, StringComparison.Ordinal))
        {
            key = Ephemeral();
            return true;
        }

        if (_cache.TryGetValue(kekId, out var cached))
        {
            key = cached;
            return true;
        }

        var reference = enc.Keks.FirstOrDefault(k => string.Equals(k.Id, kekId, StringComparison.Ordinal));
        if (reference is null || IsReservedSecret(reference.SecretRef))
        {
            return false;
        }

        try
        {
            // The provider may hand out a shared cached array, so it is never wiped.
            var secret = _secrets.GetSecretBytes(reference.SecretRef);
            if (secret is not { Length: >= MinSecretBytes })
            {
                return false;
            }

            var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt: [], info: HkdfInfo);
            key = _cache.GetOrAdd(kekId, derived);
            return true;
        }
        catch (Exception)
        {
            // Unresolvable key: unavailable (fail closed). The exception text may reference the secret name, so it is not surfaced.
            return false;
        }
    }

    private bool IsReservedSecret(string secretRef)
    {
        if (string.Equals(secretRef, WebhookKekSecretRef, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var masking = _options.CurrentValue.DataMasking.HmacSecretKeyVaultRef;
        return !string.IsNullOrWhiteSpace(masking) && string.Equals(secretRef, masking, StringComparison.Ordinal);
    }

    private byte[] Ephemeral()
    {
        lock (_ephemeralGate)
        {
            if (_ephemeral is null)
            {
                _ephemeral = RandomNumberGenerator.GetBytes(32);
                _logger.LogWarning("No CDC stream KEK is configured; an ephemeral development KEK is used and the stream becomes unreadable after a restart.");
            }

            return _ephemeral;
        }
    }
}
