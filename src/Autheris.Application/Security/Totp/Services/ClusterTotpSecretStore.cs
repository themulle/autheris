namespace Autheris.Application.Security.Totp.Services;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.State;

/// <summary>
/// Cluster-wide persistent TOTP secret store using IDistributedClusterStateProvider (Redis/Garnet)
/// with in-memory fallback for resilient single-node or partitioned operation.
/// </summary>
public sealed class ClusterTotpSecretStore : ITotpSecretStore
{
    private readonly IDistributedClusterStateProvider? _clusterState;
    private readonly ConcurrentDictionary<string, string> _localSecrets = new(StringComparer.OrdinalIgnoreCase);

    public ClusterTotpSecretStore(IDistributedClusterStateProvider? clusterState = null)
    {
        _clusterState = clusterState;
    }

    public async Task SetSecretAsync(string userSid, string secretBase32, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretBase32);

        var key = FormatKey(userSid);
        var val = secretBase32.Trim();
        _localSecrets[key] = val;

        if (_clusterState != null)
        {
            try
            {
                // Permanent until explicitly revoked/re-enrolled (e.g. 365 days TTL)
                await _clusterState.SetAsync(key, val, TimeSpan.FromDays(365), ct).ConfigureAwait(false);
            }
            catch
            {
                // Silently tolerate cluster store unreachable; local store remains consistent
            }
        }
    }

    public async Task<string?> GetSecretAsync(string userSid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userSid))
        {
            return null;
        }

        var key = FormatKey(userSid);
        if (_clusterState != null)
        {
            try
            {
                var remote = await _clusterState.GetAsync<string>(key, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(remote))
                {
                    _localSecrets[key] = remote;
                    return remote;
                }
            }
            catch
            {
                // Fall back to local secrets
            }
        }

        _localSecrets.TryGetValue(key, out var local);
        return local;
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
            try
            {
                await _clusterState.RemoveAsync(key, ct).ConfigureAwait(false);
            }
            catch
            {
                // Silently tolerate
            }
        }
    }

    private static string FormatKey(string userSid) => $"totp:secret:{userSid.Trim().ToLowerInvariant()}";
}
