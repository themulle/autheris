namespace Autheris.Infrastructure.Caching.TableCache.Storage;

using System;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Cryptographic helper for AES-256-GCM authenticated encryption and HMAC-SHA256 signature verification.
/// Conforms to SEC-BL04-05 and SEC-BL04-06.
/// </summary>
public static class TableCacheCryptoHelper
{
    public const int NonceSizeBytes = 12;
    public const int TagSizeBytes = 16;
    public const int KeySizeBytes = 32;

    private static readonly byte[] DefaultContext = Encoding.UTF8.GetBytes("Autheris.TableCache.v1");

    /// <summary>
    /// Derives a 256-bit AES-GCM data encryption key (DEK) from a master secret using HKDF-SHA256.
    /// </summary>
    public static byte[] DeriveKey(byte[] masterKey, byte[]? salt = null, byte[]? info = null)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        salt ??= Array.Empty<byte>();
        info ??= DefaultContext;

        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            masterKey,
            KeySizeBytes,
            salt,
            info);
    }

    /// <summary>
    /// Encrypts plaintext bytes using AES-256-GCM authenticated encryption.
    /// Output format: [Nonce (12 bytes)][Tag (16 bytes)][Ciphertext (N bytes)].
    /// </summary>
    public static byte[] Encrypt(byte[] key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeySizeBytes)
        {
            throw new ArgumentException($"Key must be exactly {KeySizeBytes} bytes.", nameof(key));
        }

        byte[] nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        byte[] tag = new byte[TagSizeBytes];
        byte[] ciphertext = new byte[plaintext.Length];

        using (var aesGcm = new AesGcm(key, TagSizeBytes))
        {
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }

        byte[] result = new byte[NonceSizeBytes + TagSizeBytes + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSizeBytes);
        Buffer.BlockCopy(tag, 0, result, NonceSizeBytes, TagSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSizeBytes + TagSizeBytes, ciphertext.Length);

        return result;
    }

    /// <summary>
    /// Decrypts ciphertext bytes using AES-256-GCM authenticated encryption.
    /// Verifies the 128-bit authentication tag. Tampering or corruption throws CryptographicException (SEC-BL04-05).
    /// </summary>
    public static byte[] Decrypt(byte[] key, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> associatedData = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeySizeBytes)
        {
            throw new ArgumentException($"Key must be exactly {KeySizeBytes} bytes.", nameof(key));
        }

        if (payload.Length < NonceSizeBytes + TagSizeBytes)
        {
            throw new CryptographicException("Payload is too short to contain valid AES-GCM nonce and tag.");
        }

        ReadOnlySpan<byte> nonce = payload.Slice(0, NonceSizeBytes);
        ReadOnlySpan<byte> tag = payload.Slice(NonceSizeBytes, TagSizeBytes);
        ReadOnlySpan<byte> ciphertext = payload.Slice(NonceSizeBytes + TagSizeBytes);

        byte[] plaintext = new byte[ciphertext.Length];

        using (var aesGcm = new AesGcm(key, TagSizeBytes))
        {
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        }

        return plaintext;
    }

    /// <summary>
    /// Computes HMAC-SHA256 over input data.
    /// </summary>
    public static byte[] ComputeHmac(byte[] key, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(key);
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data.ToArray());
    }

    /// <summary>
    /// Verifies HMAC-SHA256 using constant-time comparison (FixedTimeEquals).
    /// Mismatch throws SecurityException fail-closed (SEC-BL04-06).
    /// </summary>
    public static void VerifyHmacOrThrow(byte[] key, ReadOnlySpan<byte> data, ReadOnlySpan<byte> expectedHmac)
    {
        ArgumentNullException.ThrowIfNull(key);
        var actualHmac = ComputeHmac(key, data);

        if (!CryptographicOperations.FixedTimeEquals(actualHmac, expectedHmac))
        {
            throw new SecurityException("HMAC-SHA256 signature verification failed. Metadata tampering detected.");
        }
    }
}
