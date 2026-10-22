namespace Autheris.Infrastructure.Databricks;

using System;
using System.Collections.Concurrent;
using Autheris.Domain.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;

/// <summary>
/// INF-06: Bounded in-memory OAuth token cache keyed by (host, clientId, callerHash).
/// Backed by an owned MemoryCache with SizeLimit to prevent unbounded memory growth.
/// Implements IDisposable and tracks keys per (host, clientId) for targeted eviction.
/// </summary>
public sealed class DatabricksTokenCache : IDisposable
{
    private readonly MemoryCache _cache;
    private readonly ConcurrentDictionary<(string Host, string ClientId), ConcurrentDictionary<string, byte>> _keysByHostClient = new();
    private readonly int _maxEntries;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    public int EntryCount => _cache.Count;

    public DatabricksTokenCache(IOptions<GatewayOptions>? options = null, TimeProvider? timeProvider = null)
        : this(options?.Value?.Databricks?.TokenCacheMaxEntries ?? 10_000, timeProvider)
    {
    }

    public DatabricksTokenCache(int maxEntries, TimeProvider? timeProvider = null)
    {
        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "TokenCacheMaxEntries must be greater than 0.");
        }

        _maxEntries = maxEntries;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var memoryCacheOptions = new MemoryCacheOptions
        {
            SizeLimit = maxEntries,
            CompactionPercentage = 0.2,
            Clock = new TimeProviderClock(_timeProvider)
        };

        _cache = new MemoryCache(memoryCacheOptions);
    }

    private static string BuildKey(string host, string clientId, string? callerHash) =>
        $"{host.ToLowerInvariant()}|{clientId}|{callerHash ?? string.Empty}";

    internal bool TryGet(string host, string clientId, DateTimeOffset now, out string token) =>
        TryGet(host, clientId, string.Empty, now, out token);

    internal bool TryGet(string host, string clientId, string? callerHash, DateTimeOffset now, out string token)
    {
        var key = BuildKey(host, clientId, callerHash);
        if (_cache.TryGetValue(key, out string? cached) && !string.IsNullOrEmpty(cached))
        {
            token = cached;
            return true;
        }

        token = string.Empty;
        return false;
    }

    internal void Set(string host, string clientId, string token, DateTimeOffset expiresAt) =>
        Set(host, clientId, string.Empty, token, expiresAt);

    internal void Set(string host, string clientId, string? callerHash, string token, DateTimeOffset expiresAt)
    {
        var key = BuildKey(host, clientId, callerHash);
        var hostClient = (host.ToLowerInvariant(), clientId);

        var hostKeys = _keysByHostClient.GetOrAdd(hostClient, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        hostKeys.TryAdd(key, 0);

        var entryOptions = new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpiration = expiresAt
        };

        entryOptions.RegisterPostEvictionCallback((evictedKey, value, reason, state) =>
        {
            if (state is (string h, string c, string kStr))
            {
                if (_keysByHostClient.TryGetValue((h, c), out var set))
                {
                    set.TryRemove(kStr, out _);
                    if (set.IsEmpty)
                    {
                        _keysByHostClient.TryRemove((h, c), out _);
                    }
                }
            }
        }, (hostClient.Item1, hostClient.Item2, key));

        _cache.Set(key, token, entryOptions);
    }

    public void Evict(string host, string clientId, string? callerHash = null) =>
        Remove(host, clientId, callerHash);

    internal void Remove(string host, string clientId, string? callerHash = null)
    {
        var h = host.ToLowerInvariant();
        if (callerHash is not null)
        {
            var key = BuildKey(h, clientId, callerHash);
            _cache.Remove(key);
            if (_keysByHostClient.TryGetValue((h, clientId), out var set))
            {
                set.TryRemove(key, out _);
                if (set.IsEmpty)
                {
                    _keysByHostClient.TryRemove((h, clientId), out _);
                }
            }
            return;
        }

        if (_keysByHostClient.TryRemove((h, clientId), out var allKeys))
        {
            foreach (var key in allKeys.Keys)
            {
                _cache.Remove(key);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cache.Dispose();
        _keysByHostClient.Clear();
    }

    private sealed class TimeProviderClock(TimeProvider time) : ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }
}
