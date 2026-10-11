namespace Autheris.Infrastructure.Databricks;

using System.Collections.Concurrent;

/// <summary>In-memory OAuth token cache keyed by (host, clientId). Tokens are never persisted or logged (SEC-ADG-09 item 3).</summary>
public sealed class DatabricksTokenCache
{
    private readonly ConcurrentDictionary<(string Host, string ClientId), (string Token, DateTimeOffset ExpiresAt)> _entries = new();

    internal bool TryGet(string host, string clientId, DateTimeOffset now, out string token)
    {
        if (_entries.TryGetValue((host.ToLowerInvariant(), clientId), out var entry) && entry.ExpiresAt > now)
        {
            token = entry.Token;
            return true;
        }

        token = string.Empty;
        return false;
    }

    internal void Set(string host, string clientId, string token, DateTimeOffset expiresAt) =>
        _entries[(host.ToLowerInvariant(), clientId)] = (token, expiresAt);

    internal void Remove(string host, string clientId) => _entries.TryRemove((host.ToLowerInvariant(), clientId), out _);
}
