namespace Autheris.Infrastructure.Security;

using System;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Application.Webhooks;

/// <summary>
/// AES-256-GCM protection of webhook secrets (WP-ARC-A-03). The key is derived (SHA-256) from the operator-provisioned secret
/// <c>cdc-webhook-kek</c> (at least 32 bytes) resolved through <see cref="IKeyVaultSecretProvider"/>. Output format:
/// <c>enc:v1:base64(nonce[12] | tag[16] | ciphertext)</c>; AAD is <c>"{tenant}|{subscriptionId}"</c>. Nothing is ever logged.
/// </summary>
public sealed class KeyVaultWebhookSecretProtector(IKeyVaultSecretProvider secrets) : IWebhookSecretProtector
{
    public const string KekSecretRef = "cdc-webhook-kek";
    public const string Prefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MinKekBytes = 32;

    public bool IsAvailable => TryDeriveKey() is not null;

    public string Protect(string tenantId, string subscriptionId, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(secret);

        var key = TryDeriveKey() ?? throw new WebhookSecretUnavailableException();
        var plaintext = Encoding.UTF8.GetBytes(secret);
        try
        {
            var blob = new byte[NonceSize + TagSize + plaintext.Length];
            var nonce = blob.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), Aad(tenantId, subscriptionId));
            return Prefix + Convert.ToBase64String(blob);
        }
        catch (CryptographicException ex)
        {
            throw new WebhookSecretUnavailableException(ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public string Unprotect(string tenantId, string subscriptionId, string protectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        if (protectedValue is null || !protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new WebhookSecretUnavailableException();
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new WebhookSecretUnavailableException(ex);
        }

        if (blob.Length < NonceSize + TagSize)
        {
            throw new WebhookSecretUnavailableException();
        }

        var key = TryDeriveKey() ?? throw new WebhookSecretUnavailableException();
        var plaintext = new byte[blob.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), plaintext, Aad(tenantId, subscriptionId));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new WebhookSecretUnavailableException(ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Aad(string tenantId, string subscriptionId) => Encoding.UTF8.GetBytes($"{tenantId}|{subscriptionId}");

    private byte[]? TryDeriveKey()
    {
        try
        {
            // The provider may hand out a shared cached array, so it is never wiped; only the derived copy is.
            var kek = secrets.GetSecretBytes(KekSecretRef);
            return kek is { Length: >= MinKekBytes } ? SHA256.HashData(kek) : null;
        }
        catch (Exception)
        {
            // Unresolvable key: unavailable (fail closed). The exception message may reference the secret name, so it is not surfaced.
            return null;
        }
    }
}
