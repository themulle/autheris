namespace Autheris.Application.Security;

using System.Collections.Concurrent;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;

/// <summary>
/// SEC-X1-13: cache and per-issuer rate limit in front of the persistent tenant registry.
/// <list type="bullet">
/// <item>Only positive results (<see cref="TenantRegistration.Registered"/>/<see cref="TenantRegistration.Existing"/>) are cached, for a short
/// TTL and in a bounded map. A collision, a rate-limit decision or an unavailable registry is never cached as an allow, and a
/// collision evicts every cached spelling of its group. A collision recorded by another replica reaches this cache within the TTL.</item>
/// <item>A cache miss reserves one unit of the issuer's budget before the store is called and releases it when the store did not
/// register anything new, so the number of rows one issuer can create per window is bounded. When the budget is exhausted the call
/// returns <see cref="TenantRegistration.RateLimited"/> without touching the store. The number of tracked issuers is bounded; further
/// issuers share one overflow bucket.</item>
/// </list>
/// </summary>
public sealed class TenantRegistryGate(ITenantRegistry inner, TenantRegistryGateOptions options, TimeProvider? clock = null) : ITenantRegistry
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _window = options.Window ?? TimeSpan.FromMinutes(1);
    private readonly TimeSpan _ttl = options.CacheTtl ?? TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly Bucket _unknownIssuer = new();
    private readonly Bucket _overflowIssuer = new();

    private readonly record struct CacheEntry(string Key, DateTimeOffset ExpiresAt);

    private sealed class Bucket
    {
        public readonly object Sync = new();
        public DateTimeOffset WindowStart;
        public int Used;
    }

    public async ValueTask<TenantRegistration> RegisterOrGetAsync(TenantId tenant, string? issuer, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_cache.TryGetValue(tenant.Value, out var hit))
        {
            if (hit.ExpiresAt > now)
            {
                return TenantRegistration.Existing;
            }

            _cache.TryRemove(tenant.Value, out _);
        }

        var bucket = BucketFor(issuer);
        if (!TryReserve(bucket, now))
        {
            return TenantRegistration.RateLimited;
        }

        TenantRegistration result;
        try
        {
            result = await inner.RegisterOrGetAsync(tenant, issuer, ct).ConfigureAwait(false);
        }
        catch
        {
            Release(bucket);
#if DEBUG
            if (TrinoSqlEngine.CompilerTestSeams.IsDisabled(TrinoSqlEngine.SecurityControl.TenantRegistryFailClosed))
            {
                return TenantRegistration.Registered;
            }
#endif
            throw;
        }

        if (result != TenantRegistration.Registered)
        {
            Release(bucket);
        }

        var key = TenantRegistryKey.Normalize(tenant.Value);
        if (result is TenantRegistration.Registered or TenantRegistration.Existing)
        {
            if (_cache.Count < options.MaxCacheEntries)
            {
                _cache[tenant.Value] = new CacheEntry(key, now + _ttl);
            }
        }
        else if (result == TenantRegistration.Collision)
        {
            foreach (var pair in _cache)
            {
                if (pair.Value.Key == key)
                {
                    _cache.TryRemove(pair.Key, out _);
                }
            }
        }

        return result;
    }

    public ValueTask PreseedAsync(IEnumerable<string> configuredTenantIds, CancellationToken ct) =>
        inner.PreseedAsync(configuredTenantIds, ct);

    private Bucket BucketFor(string? issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer))
        {
            return _unknownIssuer;
        }

        if (_buckets.TryGetValue(issuer, out var existing))
        {
            return existing;
        }

        // Tracked issuers are bounded; a further issuer shares the overflow bucket (approximate under a race, never unbounded).
        return _buckets.Count >= options.MaxTrackedIssuers ? _overflowIssuer : _buckets.GetOrAdd(issuer, _ => new Bucket());
    }

    private bool TryReserve(Bucket bucket, DateTimeOffset now)
    {
        lock (bucket.Sync)
        {
            if (now - bucket.WindowStart >= _window)
            {
                bucket.WindowStart = now;
                bucket.Used = 0;
            }

            if (bucket.Used >= options.MaxNewRegistrationsPerIssuerPerWindow)
            {
                return false;
            }

            bucket.Used++;
            return true;
        }
    }

    private static void Release(Bucket bucket)
    {
        lock (bucket.Sync)
        {
            if (bucket.Used > 0)
            {
                bucket.Used--;
            }
        }
    }
}

public sealed record TenantRegistryGateOptions(
    int MaxNewRegistrationsPerIssuerPerWindow = 20,
    TimeSpan? Window = null,
    TimeSpan? CacheTtl = null,
    int MaxCacheEntries = 10_000,
    int MaxTrackedIssuers = 1_024);
