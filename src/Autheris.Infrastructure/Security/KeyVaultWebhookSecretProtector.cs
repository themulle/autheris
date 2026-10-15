namespace Autheris.Infrastructure.Security;

using System;
using System.Threading;
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
    // Derived once per process: the key provider is not asked (and does not log) per call. Rotation = provision a new KEK and restart;
    // v1 values carry no key id, so existing subscriptions must be re-created after a rotation.
    private readonly Lazy<byte[]?> _key = new(() => DeriveKey(secrets), LazyThreadSafetyMode.ExecutionAndPublication);

    public const string KekSecretRef = "cdc-webhook-kek";
    public const string Prefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MinKekBytes = 32;

    public bool IsAvailable => _key.Value is not null;

    public string Protect(string tenantId, string subscriptionId, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(secret);

        var key = (byte[]?)_key.Value?.Clone() ?? throw new WebhookSecretUnavailableException();
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

        var key = (byte[]?)_key.Value?.Clone() ?? throw new WebhookSecretUnavailableException();
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

    private static byte[]? DeriveKey(IKeyVaultSecretProvider secrets)
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
