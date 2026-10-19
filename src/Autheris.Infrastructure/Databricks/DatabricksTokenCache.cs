namespace Autheris.Infrastructure.Databricks;

using System;
using System.Collections.Concurrent;

/// <summary>
/// In-memory OAuth token cache keyed by (host, clientId, callerHash). Tokens are never persisted or logged (SEC-ADG-09, SEC-OBO238-03).
/// For OBO tokens, callerHash is the SHA-256 hash of the caller assertion to ensure strict per-user cache isolation.
/// </summary>
public sealed class DatabricksTokenCache
{
    private readonly ConcurrentDictionary<(string Host, string ClientId, string CallerHash), (string Token, DateTimeOffset ExpiresAt)> _entries = new();

    internal bool TryGet(string host, string clientId, DateTimeOffset now, out string token) =>
        TryGet(host, clientId, string.Empty, now, out token);

    internal bool TryGet(string host, string clientId, string? callerHash, DateTimeOffset now, out string token)
    {
        var key = (host.ToLowerInvariant(), clientId, callerHash ?? string.Empty);
        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > now)
        {
            token = entry.Token;
            return true;
        }

        token = string.Empty;
        return false;
    }

    internal void Set(string host, string clientId, string token, DateTimeOffset expiresAt) =>
        Set(host, clientId, string.Empty, token, expiresAt);

    internal void Set(string host, string clientId, string? callerHash, string token, DateTimeOffset expiresAt)
    {
        var key = (host.ToLowerInvariant(), clientId, callerHash ?? string.Empty);
        _entries[key] = (token, expiresAt);
    }

    internal void Remove(string host, string clientId, string? callerHash = null)
    {
        var h = host.ToLowerInvariant();
        if (callerHash is not null)
        {
            _entries.TryRemove((h, clientId, callerHash), out _);
            return;
        }

        foreach (var key in _entries.Keys)
        {
            if (string.Equals(key.Host, h, StringComparison.Ordinal) && string.Equals(key.ClientId, clientId, StringComparison.Ordinal))
            {
                _entries.TryRemove(key, out _);
            }
        }
    }
}
