namespace Autheris.GraphQL.Federation;

using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Validates cryptographic HMAC-SHA256 signatures on forwarded Zero-Trust context headers (1.10).
/// Protects downstream subgraphs against context spoofing and replay attacks.
/// </summary>
public static class SubgraphSecurityValidator
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> SeenNonces = new(StringComparer.Ordinal);

    /// <summary>
    /// Clears the internal replay cache (primarily for unit tests and diagnostics).
    /// </summary>
    public static void ResetReplayCache() => SeenNonces.Clear();

    public static bool ValidateSignature(
        string? tenant,
        string? userSid,
        string? timestampStr,
        string? nonce,
        string? signature,
        string signingKey,
        TimeSpan? maxDrift = null,
        string? subgraph = null,
        string? roles = null,
        string? method = null,
        string? path = null)
    {
        // SEC-MED-23: Disallow 4-field signature downgrade; target subgraph must be specified
        if (string.IsNullOrWhiteSpace(tenant) ||
            string.IsNullOrWhiteSpace(timestampStr) ||
            string.IsNullOrWhiteSpace(nonce) ||
            string.IsNullOrWhiteSpace(signature) ||
            string.IsNullOrWhiteSpace(signingKey) ||
            string.IsNullOrWhiteSpace(subgraph))
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

        // Full 8-field context payload
        var payload = $"{tenant}:{userSid ?? string.Empty}:{timestampStr}:{nonce}:{subgraph}:{roles ?? string.Empty}:{method ?? string.Empty}:{path ?? string.Empty}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
        var expectedSignature = Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));

        var isSignatureValid = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(expectedSignature));

        if (!isSignatureValid)
        {
            return false;
        }

        // SEC-MED-23: Nonce replay defense within time-drift window
        var replayKey = $"{tenant}:{nonce}";
        if (!SeenNonces.TryAdd(replayKey, now))
        {
            return false;
        }

        if (SeenNonces.Count > 10_000)
        {
            var cutoff = now - TimeSpan.FromMinutes(10);
            foreach (var kvp in SeenNonces)
            {
                if (kvp.Value < cutoff)
                {
                    SeenNonces.TryRemove(kvp.Key, out _);
                }
            }
        }

        return true;
    }
}
