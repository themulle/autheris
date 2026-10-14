namespace Autheris.GraphQL.Federation;

using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Federation.Services;
using Autheris.Application.State;

/// <summary>
/// Validates cryptographic HMAC-SHA256 signatures on forwarded Zero-Trust context headers (1.10).
/// Protects downstream subgraphs against context spoofing and replay attacks.
/// SR-M-18: only the versioned v2 canonical form (length-prefixed fields, body hash) is accepted unless
/// <c>allowLegacy</c> is set explicitly for a rolling upgrade.
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
        string? path = null,
        string? version = null,
        string? bodySha256 = null,
        bool allowLegacy = false)
    {
        var drift = maxDrift ?? TimeSpan.FromSeconds(60);
        if (!VerifyCore(tenant, userSid, timestampStr, nonce, signature, signingKey, drift, subgraph, roles, method, path, version, bodySha256, allowLegacy, out var now))
        {
            return false;
        }

        return TryRegisterNonceLocal(tenant!, nonce!, now);
    }

    /// <summary>
    /// Same as <see cref="ValidateSignature"/> but records the nonce in the distributed cluster state provider (when given and
    /// reachable) so replays are detected across nodes; falls back to the process-local cache when the store is unavailable
    /// unless <paramref name="topology"/> reports a cluster-wide state, in which case the request is rejected.
    /// </summary>
    public static async Task<bool> ValidateSignatureAsync(
        string? tenant,
        string? userSid,
        string? timestampStr,
        string? nonce,
        string? signature,
        string signingKey,
        IDistributedClusterStateProvider? clusterState,
        TimeSpan? maxDrift = null,
        string? subgraph = null,
        string? roles = null,
        string? method = null,
        string? path = null,
        string? version = null,
        string? bodySha256 = null,
        bool allowLegacy = false,
        IClusterStateTopology? topology = null,
        CancellationToken ct = default)
    {
        var drift = maxDrift ?? TimeSpan.FromSeconds(60);
        if (!VerifyCore(tenant, userSid, timestampStr, nonce, signature, signingKey, drift, subgraph, roles, method, path, version, bodySha256, allowLegacy, out var now))
        {
            return false;
        }

        if (!TryRegisterNonceLocal(tenant!, nonce!, now))
        {
            return false;
        }

        if (clusterState != null)
        {
            // WP-ARC-A-09: with a cluster-wide topology the shared nonce registry is the replay defense, so an unavailable
            // store rejects the request (fail closed). Without it the process-local cache above is all there is.
            var failClosed = topology?.IsClusterWide == true;
            try
            {
                var ttl = drift * 2 + TimeSpan.FromSeconds(5);
                var count = await clusterState.IncrementAsync($"federation:nonce:{tenant}:{nonce}", 1, ttl, ct).ConfigureAwait(false);
                if (count is > 1)
                {
                    return false;
                }

                if (count is null && failClosed)
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (failClosed)
                {
                    return false;
                }

                // Shared store unavailable and not cluster-wide: the local cache above remains the replay defense.
            }
        }

        return true;
    }

    private static bool VerifyCore(
        string? tenant, string? userSid, string? timestampStr, string? nonce, string? signature, string signingKey,
        TimeSpan drift, string? subgraph, string? roles, string? method, string? path,
        string? version, string? bodySha256, bool allowLegacy, out DateTimeOffset now)
    {
        now = DateTimeOffset.UtcNow;

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

        // SR-L-17: out-of-range timestamps are invalid, not an exception.
        if (timestamp < DateTimeOffset.MinValue.ToUnixTimeSeconds() || timestamp > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return false;
        }

        var messageTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if (now - messageTime > drift || messageTime - now > drift)
        {
            return false;
        }

        string payload;
        if (string.Equals(version, FederationContextSignature.CurrentVersion, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(bodySha256))
            {
                return false;
            }

            payload = FederationContextSignature.BuildCanonicalV2(
                tenant, userSid, timestampStr, nonce, subgraph, roles, method, path, bodySha256.ToLowerInvariant());
        }
        else if (allowLegacy && string.IsNullOrWhiteSpace(version))
        {
            payload = FederationContextSignature.BuildLegacy(tenant, userSid, timestampStr, nonce, subgraph, roles, method, path);
        }
        else
        {
            return false;
        }

        var expectedSignature = FederationContextSignature.Sign(payload, signingKey);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(expectedSignature));
    }

    private static bool TryRegisterNonceLocal(string tenant, string nonce, DateTimeOffset now)
    {
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
