namespace Autheris.Application.Security.Totp.Services;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security.Totp.Interfaces;

/// <summary>
/// Fast in-memory implementation of ITotpSecretStore for single-node deployments and tests.
/// </summary>
public sealed class InMemoryTotpSecretStore : ITotpSecretStore
{
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);

    public Task SetSecretAsync(string userSid, string secretBase32, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretBase32);

        _secrets[userSid.Trim()] = secretBase32.Trim();
        return Task.CompletedTask;
    }

    public Task<string?> GetSecretAsync(string userSid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userSid))
        {
            return Task.FromResult<string?>(null);
        }

        _secrets.TryGetValue(userSid.Trim(), out var secret);
        return Task.FromResult<string?>(secret);
    }

    public Task<bool> HasSecretAsync(string userSid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userSid))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(_secrets.ContainsKey(userSid.Trim()));
    }

    public Task RemoveSecretAsync(string userSid, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(userSid))
        {
            _secrets.TryRemove(userSid.Trim(), out _);
        }

        return Task.CompletedTask;
    }
}
