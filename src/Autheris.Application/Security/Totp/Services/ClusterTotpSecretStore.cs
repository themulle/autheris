namespace Autheris.Application.Security.Totp.Services;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.State;

/// <summary>
/// Cluster-wide persistent TOTP secret store using IDistributedClusterStateProvider (Redis/Garnet).
/// Without a cluster state provider the secrets live in-process (single node).
/// SR-M-06: when a cluster provider is configured every read goes to the shared store (no per-node cache), so a
/// revocation or overwrite is effective on all nodes immediately, and shared-store failures are not swallowed
/// (reads, writes and revocations fail closed instead of silently diverging from the cluster).
/// SR-L-05: seeds are optionally encrypted at rest through <see cref="ITotpSecretProtector"/>.
/// </summary>
public sealed class ClusterTotpSecretStore : ITotpSecretStore
{
    internal const string ProtectedPrefix = "dp1:";

    private readonly IDistributedClusterStateProvider? _clusterState;
    private readonly ITotpSecretProtector? _protector;
    private readonly ConcurrentDictionary<string, string> _localSecrets = new(StringComparer.OrdinalIgnoreCase);

    public ClusterTotpSecretStore(
        IDistributedClusterStateProvider? clusterState = null,
        ITotpSecretProtector? protector = null)
    {
        _clusterState = clusterState;
        _protector = protector;
    }

    public async Task SetSecretAsync(string userSid, string secretBase32, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretBase32);

        var key = FormatKey(userSid);
        var val = secretBase32.Trim();

        if (_clusterState == null)
        {
            _localSecrets[key] = val;
            return;
        }

        var stored = _protector != null ? ProtectedPrefix + _protector.Protect(val) : val;

        // Permanent until explicitly revoked/re-enrolled (e.g. 365 days TTL)
        await _clusterState.SetAsync(key, stored, TimeSpan.FromDays(365), ct).ConfigureAwait(false);
    }

    public async Task<string?> GetSecretAsync(string userSid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userSid))
        {
            return null;
        }

        var key = FormatKey(userSid);
        if (_clusterState == null)
        {
            _localSecrets.TryGetValue(key, out var local);
            return local;
        }

        var remote = await _clusterState.GetAsync<string>(key, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(remote))
        {
            return null;
        }

        if (remote.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            if (_protector == null)
            {
                throw new InvalidOperationException(
                    "A TOTP secret is stored encrypted but no ITotpSecretProtector is configured (Totp.EncryptSecretsAtRest).");
            }

            return _protector.Unprotect(remote[ProtectedPrefix.Length..]);
        }

        // Legacy plaintext value (written before encryption at rest was enabled).
        return remote;
    }

    public async Task<bool> HasSecretAsync(string userSid, CancellationToken ct = default)
    {
        var secret = await GetSecretAsync(userSid, ct).ConfigureAwait(false);
        return !string.IsNullOrEmpty(secret);
    }

    public async Task RemoveSecretAsync(string userSid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userSid))
        {
            return;
        }

        var key = FormatKey(userSid);
        _localSecrets.TryRemove(key, out _);

        if (_clusterState != null)
        {
            await _clusterState.RemoveAsync(key, ct).ConfigureAwait(false);
        }
    }

    private static string FormatKey(string userSid) => $"totp:secret:{userSid.Trim().ToLowerInvariant()}";
}
