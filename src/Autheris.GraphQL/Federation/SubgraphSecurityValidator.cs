namespace Autheris.GraphQL.Federation;

using System;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Validates cryptographic HMAC-SHA256 signatures on forwarded Zero-Trust context headers (1.10).
/// Protects downstream subgraphs against context spoofing and replay attacks.
/// </summary>
public static class SubgraphSecurityValidator
{
    public static bool ValidateSignature(
        string? tenant,
        string? userSid,
        string? timestampStr,
        string? nonce,
        string? signature,
        string signingKey,
        TimeSpan? maxDrift = null)
    {
        if (string.IsNullOrWhiteSpace(tenant) ||
            string.IsNullOrWhiteSpace(timestampStr) ||
            string.IsNullOrWhiteSpace(nonce) ||
            string.IsNullOrWhiteSpace(signature) ||
            string.IsNullOrWhiteSpace(signingKey))
        {
            return false;
        }

        if (!long.TryParse(timestampStr, out var timestamp))
        {
            return false;
        }

        var drift = maxDrift ?? TimeSpan.FromSeconds(60);
        var messageTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        var now = DateTimeOffset.UtcNow;
        if (now - messageTime > drift || messageTime - now > drift)
        {
            return false;
        }

        var payload = $"{tenant}:{userSid ?? string.Empty}:{timestampStr}:{nonce}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
        var expectedSignature = Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(expectedSignature));
    }
}
