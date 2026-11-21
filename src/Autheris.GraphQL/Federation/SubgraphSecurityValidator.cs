namespace Autheris.GraphQL.Federation;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Federation.Services;
using Autheris.Application.State;

/// <summary>
/// Validates cryptographic HMAC-SHA256 signatures on forwarded Zero-Trust context headers (1.10).
/// Protects downstream subgraphs against context spoofing and replay attacks.
/// SR-M-18: only the versioned v2 canonical form (length-prefixed fields, body hash) is accepted.
/// </summary>
public static class SubgraphSecurityValidator
{
    private const int MaxCachedNonces = 10_000;
    private static readonly ConcurrentDictionary<string, DateTimeOffset> SeenNonces = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<(string Key, DateTimeOffset ExpiresAt)> NonceOrder = new();
    private static readonly object PruneGate = new();
    private static long _lastFullSweepTimestamp;

    internal static int CachedNonceCount => SeenNonces.Count;

    /// <summary>
    /// Clears the internal replay cache (primarily for unit tests and diagnostics).
    /// </summary>
    public static void ResetReplayCache()
    {
        lock (PruneGate)
        {
            SeenNonces.Clear();
            while (NonceOrder.TryDequeue(out _)) { }
            _lastFullSweepTimestamp = 0;
        }
    }

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
        string? bodySha256 = null)
    {
        var drift = maxDrift ?? TimeSpan.FromSeconds(60);
        if (!VerifyCore(tenant, userSid, timestampStr, nonce, signature, signingKey, drift, subgraph, roles, method, path, version, bodySha256, out var now, out var messageTime))
        {
            return false;
        }

        return TryRegisterNonceLocal(tenant!, nonce!, now, messageTime, drift);
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
        IClusterStateTopology? topology = null,
        CancellationToken ct = default)
    {
        var drift = maxDrift ?? TimeSpan.FromSeconds(60);
        if (!VerifyCore(tenant, userSid, timestampStr, nonce, signature, signingKey, drift, subgraph, roles, method, path, version, bodySha256, out var now, out var messageTime))
        {
            return false;
        }

        if (!TryRegisterNonceLocal(tenant!, nonce!, now, messageTime, drift))
        {
            return false;
        }

        if (clusterState != null)
        {
            // with a cluster-wide topology the shared nonce registry is the replay defense, so an unavailable
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
        string? version, string? bodySha256, out DateTimeOffset now, out DateTimeOffset messageTime)
    {
        now = DateTimeOffset.UtcNow;
        messageTime = default;

        // Disallow 4-field signature downgrade; target subgraph must be specified
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

        messageTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if (now - messageTime > drift || messageTime - now > drift)
        {
            return false;
        }

        if (!string.Equals(version, FederationContextSignature.CurrentVersion, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(bodySha256))
        {
            return false;
        }

        var payload = FederationContextSignature.BuildCanonicalV2(
            tenant, userSid, timestampStr, nonce, subgraph, roles, method, path, bodySha256.ToLowerInvariant());

        var expectedSignature = FederationContextSignature.Sign(payload, signingKey);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(expectedSignature));
    }

    private static bool TryRegisterNonceLocal(string tenant, string nonce, DateTimeOffset now, DateTimeOffset messageTime, TimeSpan drift)
    {
        var effectiveDrift = drift > TimeSpan.Zero ? drift : TimeSpan.Zero;
        var expiresAt = (messageTime > now ? messageTime : now) + effectiveDrift;
        var replayKey = $"{tenant}:{nonce}";

        if (SeenNonces.TryGetValue(replayKey, out var existingExpiry))
        {
            if (existingExpiry >= now)
            {
                return false;
            }

            if (!SeenNonces.TryUpdate(replayKey, expiresAt, existingExpiry))
            {
                return false;
            }
        }
        else if (!SeenNonces.TryAdd(replayKey, expiresAt))
        {
            return false;
        }

        NonceOrder.Enqueue((replayKey, expiresAt));
        PruneExpiredOrOverflow(now);
        return true;
    }

    private static void PruneExpiredOrOverflow(DateTimeOffset now)
    {
        if (!Monitor.TryEnter(PruneGate))
        {
            return;
        }

        try
        {
            for (int i = 0; i < 16 && NonceOrder.TryPeek(out var head) && head.ExpiresAt < now; i++)
            {
                if (NonceOrder.TryDequeue(out var popped))
                {
                    SeenNonces.TryRemove(new KeyValuePair<string, DateTimeOffset>(popped.Key, popped.ExpiresAt));
                }
            }

            if (SeenNonces.Count > MaxCachedNonces)
            {
                long stamp = Stopwatch.GetTimestamp();
                if (_lastFullSweepTimestamp == 0 || Stopwatch.GetElapsedTime(_lastFullSweepTimestamp, stamp) >= TimeSpan.FromSeconds(1))
                {
                    _lastFullSweepTimestamp = stamp;
                    foreach (var kvp in SeenNonces)
                    {
                        if (kvp.Value < now)
                        {
                            SeenNonces.TryRemove(new KeyValuePair<string, DateTimeOffset>(kvp.Key, kvp.Value));
                        }
                    }
                }

                while (SeenNonces.Count > MaxCachedNonces && NonceOrder.TryDequeue(out var oldest))
                {
                    SeenNonces.TryRemove(new KeyValuePair<string, DateTimeOffset>(oldest.Key, oldest.ExpiresAt));
                }
            }
        }
        finally
        {
            Monitor.Exit(PruneGate);
        }
    }
}
