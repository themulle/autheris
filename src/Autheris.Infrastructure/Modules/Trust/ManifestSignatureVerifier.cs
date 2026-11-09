namespace Autheris.Infrastructure.Modules.Trust;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Verifies ECDSA P-256 (NIST P-256 / ES256) signatures over canonicalized module manifests.
/// </summary>
public static class ManifestSignatureVerifier
{
    /// <summary>
    /// Computes the 16-character lowercase hex key identifier (first 8 bytes of SHA-256 over the SPKI public key).
    /// </summary>
    public static string ComputeKeyId(ECDsa publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        byte[] spki = publicKey.ExportSubjectPublicKeyInfo();
        byte[] hash = SHA256.HashData(spki);
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>
    /// Verifies an ECDSA P-256 SHA-256 DER signature (<see cref="DSASignatureFormat.Rfc3279DerSequence"/>)
    /// over <paramref name="canonicalBytes"/>.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> canonicalBytes, string signatureBase64, ECDsa publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        if (canonicalBytes.IsEmpty || string.IsNullOrWhiteSpace(signatureBase64))
        {
            return false;
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        if (signatureBytes.Length == 0)
        {
            return false;
        }

        try
        {
            return publicKey.VerifyData(
                canonicalBytes,
                signatureBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Signs <paramref name="canonicalBytes"/> using ECDSA P-256 SHA-256 in RFC 3279 DER sequence format
    /// and returns the Base64-encoded signature.
    /// </summary>
    public static string Sign(ReadOnlySpan<byte> canonicalBytes, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        byte[] sig = privateKey.SignData(
            canonicalBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        return Convert.ToBase64String(sig);
    }

    /// <summary>
    /// Verifies <paramref name="manifest"/> against the key of <paramref name="keyring"/> selected by the manifest
    /// <c>keyId</c>. An empty keyring and a missing or unknown <c>keyId</c> are rejected.
    /// </summary>
    public static bool VerifyManifest(
        ModuleManifest manifest,
        IReadOnlyDictionary<string, ECDsa> keyring,
        out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(keyring);

        if (keyring.Count == 0)
        {
            failureReason = "module signing keyring is empty: no trusted signing key is available";
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.KeyId))
        {
            failureReason = "manifest has no keyId";
            return false;
        }

        if (!keyring.TryGetValue(manifest.KeyId, out var publicKey))
        {
            failureReason = $"unknown or mismatched keyId '{manifest.KeyId}'";
            return false;
        }

        return VerifyManifest(manifest, publicKey, out failureReason);
    }

    /// <summary>
    /// Verifies both the <c>keyId</c> and the cryptographic signature of <paramref name="manifest"/> against <paramref name="publicKey"/>.
    /// </summary>
    public static bool VerifyManifest(ModuleManifest manifest, ECDsa publicKey, out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(publicKey);

        if (manifest.Schema != 1)
        {
            failureReason = $"unsupported manifest schema '{manifest.Schema}'";
            return false;
        }

        string expectedKeyId = ComputeKeyId(publicKey);
        byte[] expectedKeyBytes = Encoding.UTF8.GetBytes(expectedKeyId);
        byte[] actualKeyBytes = Encoding.UTF8.GetBytes(manifest.KeyId ?? string.Empty);

        if (!CryptographicOperations.FixedTimeEquals(expectedKeyBytes, actualKeyBytes))
        {
            failureReason = $"unknown or mismatched keyId '{manifest.KeyId}'";
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.Signature))
        {
            failureReason = "missing manifest signature";
            return false;
        }

        byte[] canonicalBytes = ManifestCanonicalizer.ToCanonicalBytes(manifest);
        if (!Verify(canonicalBytes, manifest.Signature, publicKey))
        {
            failureReason = "manifest signature verification failed";
            return false;
        }

        failureReason = null;
        return true;
    }
}
