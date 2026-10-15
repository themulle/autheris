namespace Autheris.Application.Streaming.Crypto;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// AES-256-GCM envelope encryption of stream events and row images (PLAN-CDC-99 D-A8). Events are bound by AAD to stream id, offset,
/// kind, tenant scope, tenant id and prior tenant id (SEC-CDC99-01): a payload moved to another position or tenant fails authentication.
/// An authentication failure is an integrity incident (SEC-CDC99-16): counted, logged with stream id and offset only, and surfaced as
/// <see cref="CdcStreamKeyUnavailableException"/> so that consumers fail closed without advancing.
/// </summary>
public sealed class CdcStreamCrypto : ICdcStreamCrypto
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MaxLoggedIncidents = 1024;

    private static readonly Counter<long> IntegrityFailures = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_cdc_stream_integrity_failures_total",
        description: "CDC stream payloads that failed authenticated decryption (tamper, moved ciphertext or wrong key)");

    private readonly CdcStreamKeyring _keyring;
    private readonly ILogger<CdcStreamCrypto> _logger;
    private readonly ConcurrentDictionary<string, byte> _loggedIncidents = new(StringComparer.Ordinal);

    public CdcStreamCrypto(CdcStreamKeyring keyring, ILogger<CdcStreamCrypto> logger)
    {
        _keyring = keyring ?? throw new ArgumentNullException(nameof(keyring));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<CdcProtectedBlob> ProtectEventAsync(
        CdcStreamId id, long offset, CdcStreamEventKind kind, CdcTenantScope scope, string? tenantId, string? priorTenantId,
        ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
    {
        var dek = await _keyring.AcquireForEncryptionAsync(id, CdcKeyPurpose.Payload, ct).ConfigureAwait(false);
        return Seal(dek, plaintext.Span, EventAad(id, offset, kind, scope, tenantId, priorTenantId));
    }

    public async ValueTask<byte[]> UnprotectEventAsync(CdcStreamEventRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var aad = EventAad(record.StreamId, record.Offset, record.Kind, record.TenantScope, record.TenantId, record.PriorTenantId);
        return await OpenAsync(record.StreamId, record.Payload, aad, record.Offset, ct).ConfigureAwait(false);
    }

    public async ValueTask<CdcProtectedBlob> ProtectImageAsync(CdcStreamId id, byte[] fingerprint, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var dek = await _keyring.AcquireForEncryptionAsync(id, CdcKeyPurpose.Image, ct).ConfigureAwait(false);
        return Seal(dek, plaintext.Span, ImageAad(id, fingerprint));
    }

    public async ValueTask<byte[]> UnprotectImageAsync(CdcStreamId id, CdcRowImageRecord image, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        return await OpenAsync(id, image.Image, ImageAad(id, image.KeyFingerprint), null, ct).ConfigureAwait(false);
    }

    public async ValueTask<byte[]> FingerprintAsync(CdcStreamId id, IReadOnlyList<object?> keyValues, CancellationToken ct = default)
    {
        var key = await _keyring.GetFingerprintKeyAsync(id, ct).ConfigureAwait(false);
        try
        {
            return HMACSHA256.HashData(key.Key, CdcPayloadCodec.CanonicalKey(keyValues));
        }
        catch (CdcPayloadCodecException)
        {
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key.Key);
        }
    }

    /// <summary>AAD of an event payload: <c>cdc-evt|v1|stream|offset|kind|scope|tenant|priorTenant</c> (numeric enums, stable under renames).</summary>
    public static byte[] EventAad(CdcStreamId id, long offset, CdcStreamEventKind kind, CdcTenantScope scope, string? tenantId, string? priorTenantId) =>
        Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"cdc-evt|v1|{id.Value}|{offset}|{(int)kind}|{(int)scope}|{tenantId ?? string.Empty}|{priorTenantId ?? string.Empty}"));

    /// <summary>AAD of a row image: <c>cdc-img|v1|stream|fingerprintHex</c>.</summary>
    public static byte[] ImageAad(CdcStreamId id, byte[] fingerprint) =>
        Encoding.UTF8.GetBytes($"cdc-img|v1|{id.Value}|{Convert.ToHexStringLower(fingerprint)}");

    private static CdcProtectedBlob Seal(CdcDek dek, ReadOnlySpan<byte> plaintext, byte[] aad)
    {
        try
        {
            var blob = new byte[NonceSize + TagSize + plaintext.Length];
            RandomNumberGenerator.Fill(blob.AsSpan(0, NonceSize));
            using var aes = new AesGcm(dek.Key, TagSize);
            aes.Encrypt(blob.AsSpan(0, NonceSize), plaintext, blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), aad);
            return new CdcProtectedBlob(dek.DekId, blob);
        }
        catch (CryptographicException)
        {
            throw new CdcStreamKeyUnavailableException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek.Key);
        }
    }

    private async ValueTask<byte[]> OpenAsync(CdcStreamId id, CdcProtectedBlob blob, byte[] aad, long? offset, CancellationToken ct)
    {
        if (blob?.Data is null || blob.Data.Length < NonceSize + TagSize)
        {
            RecordIntegrityFailure(id, offset);
            throw new CdcStreamKeyUnavailableException();
        }

        var key = await _keyring.GetDekAsync(id, blob.DekId, ct).ConfigureAwait(false);
        var plaintext = new byte[blob.Data.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(blob.Data.AsSpan(0, NonceSize), blob.Data.AsSpan(NonceSize + TagSize), blob.Data.AsSpan(NonceSize, TagSize), plaintext, aad);
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            RecordIntegrityFailure(id, offset);
            throw new CdcStreamKeyUnavailableException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private void RecordIntegrityFailure(CdcStreamId id, long? offset)
    {
        IntegrityFailures.Add(1, new KeyValuePair<string, object?>("stream", id.Value));
        if (_loggedIncidents.Count >= MaxLoggedIncidents)
        {
            _loggedIncidents.Clear();
        }

        var key = $"{id.Value}/{offset?.ToString(CultureInfo.InvariantCulture) ?? "img"}";
        if (_loggedIncidents.TryAdd(key, 0))
        {
            _logger.LogError("CDC stream integrity failure: payload authentication failed (stream {StreamId}, offset {Offset}).", id.Value, offset);
        }
    }
}
