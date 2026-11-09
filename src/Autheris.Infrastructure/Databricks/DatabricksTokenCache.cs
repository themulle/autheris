namespace Autheris.Infrastructure.Databricks;

using System;
using System.Collections.Concurrent;
using Autheris.Domain.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;

/// <summary>
/// Bounded in-memory OAuth and PAT token cache keyed by (dataSourceName, tenantId, mode, principalId, callerHash).
/// Backed by an owned MemoryCache with SizeLimit to prevent unbounded memory growth.
/// Implements IDisposable and tracks keys per (dataSource, tenant) for targeted eviction.
/// </summary>
public sealed class DatabricksTokenCache : IDisposable
{
    private readonly MemoryCache _cache;
    private readonly ConcurrentDictionary<(string DataSource, string TenantId), ConcurrentDictionary<string, byte>> _keysBySourceTenant = new();
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

    public static string BuildKey(string dataSourceName, string? tenantId, DatabricksAuthMode mode, string principalId, string? callerHash) =>
        $"{dataSourceName.ToLowerInvariant()}|{tenantId ?? "-"}|{mode}|{principalId}|{callerHash ?? string.Empty}";

    internal bool TryGet(string dataSourceName, string? tenantId, DatabricksAuthMode mode, string principalId, string? callerHash, DateTimeOffset now, out string token)
    {
        var key = BuildKey(dataSourceName, tenantId, mode, principalId, callerHash);
        if (_cache.TryGetValue(key, out string? cached) && !string.IsNullOrEmpty(cached))
        {
            token = cached;
            return true;
        }

        token = string.Empty;
        return false;
    }

    internal void Set(string dataSourceName, string? tenantId, DatabricksAuthMode mode, string principalId, string? callerHash, string token, DateTimeOffset expiresAt)
    {
        var key = BuildKey(dataSourceName, tenantId, mode, principalId, callerHash);
        var group = (dataSourceName.ToLowerInvariant(), tenantId ?? "-");

        var sourceKeys = _keysBySourceTenant.GetOrAdd(group, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        sourceKeys.TryAdd(key, 0);

        var entryOptions = new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpiration = expiresAt
        };

        entryOptions.RegisterPostEvictionCallback((evictedKey, value, reason, state) =>
        {
            if (state is (string ds, string tid, string kStr))
            {
                if (_keysBySourceTenant.TryGetValue((ds, tid), out var set))
                {
                    set.TryRemove(kStr, out _);
                    if (set.IsEmpty)
                    {
                        _keysBySourceTenant.TryRemove((ds, tid), out _);
                    }
                }
            }
        }, (group.Item1, group.Item2, key));

        _cache.Set(key, token, entryOptions);
    }

    public void Evict(string dataSourceName, string? tenantOrPrincipalId = null)
    {
        var ds = dataSourceName.ToLowerInvariant();
        if (tenantOrPrincipalId is null)
        {
            foreach (var keyPair in _keysBySourceTenant.Keys)
            {
                if (string.Equals(keyPair.DataSource, ds, StringComparison.OrdinalIgnoreCase))
                {
                    if (_keysBySourceTenant.TryRemove(keyPair, out var keys))
                    {
                        foreach (var key in keys.Keys)
                        {
                            _cache.Remove(key);
                        }
                    }
                }
            }
            return;
        }

        var group = (ds, tenantOrPrincipalId);
        if (_keysBySourceTenant.TryRemove(group, out var tenantKeys))
        {
            foreach (var key in tenantKeys.Keys)
            {
                _cache.Remove(key);
            }
        }

        foreach (var keyPair in _keysBySourceTenant.Keys)
        {
            if (string.Equals(keyPair.DataSource, ds, StringComparison.OrdinalIgnoreCase))
            {
                if (_keysBySourceTenant.TryGetValue(keyPair, out var set))
                {
                    foreach (var key in set.Keys)
                    {
                        var parts = key.Split('|');
                        if (parts.Length >= 4 && string.Equals(parts[3], tenantOrPrincipalId, StringComparison.Ordinal))
                        {
                            _cache.Remove(key);
                            set.TryRemove(key, out _);
                        }
                    }
                }
            }
        }
    }

    internal void Remove(string dataSourceName, string? tenantId, DatabricksAuthMode mode, string principalId, string? callerHash = null)
    {
        var key = BuildKey(dataSourceName, tenantId, mode, principalId, callerHash);
        _cache.Remove(key);
        var group = (dataSourceName.ToLowerInvariant(), tenantId ?? "-");
        if (_keysBySourceTenant.TryGetValue(group, out var set))
        {
            set.TryRemove(key, out _);
            if (set.IsEmpty)
            {
                _keysBySourceTenant.TryRemove(group, out _);
            }
        }
    }

    internal bool TryGet(string host, string clientId, DateTimeOffset now, out string token) =>
        TryGet(host, null, DatabricksAuthMode.OAuthM2M, clientId, string.Empty, now, out token);

    internal bool TryGet(string host, string clientId, string? callerHash, DateTimeOffset now, out string token) =>
        TryGet(host, null, DatabricksAuthMode.OAuthM2M, clientId, callerHash, now, out token);

    internal void Set(string host, string clientId, string token, DateTimeOffset expiresAt) =>
        Set(host, null, DatabricksAuthMode.OAuthM2M, clientId, string.Empty, token, expiresAt);

    internal void Set(string host, string clientId, string? callerHash, string token, DateTimeOffset expiresAt) =>
        Set(host, null, DatabricksAuthMode.OAuthM2M, clientId, callerHash, token, expiresAt);

    public void Evict(string host, string clientId, string? callerHash)
    {
        if (callerHash is not null)
        {
            Remove(host, null, DatabricksAuthMode.OAuthM2M, clientId, callerHash);
            return;
        }

        Evict(host, clientId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cache.Dispose();
        _keysBySourceTenant.Clear();
    }

    private sealed class TimeProviderClock(TimeProvider time) : ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }
}
