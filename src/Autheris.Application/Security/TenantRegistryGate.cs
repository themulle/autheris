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
/// <item>A cache miss first asks the store whether the tenant already exists (a read that neither writes nor costs budget): an existing
/// tenant is answered from the lookup, so an issuer that exhausted its budget for new tenants can still serve the tenants it already has.
/// Only an unknown tenant reserves one unit of the issuer's budget before the store registers it, and the unit is released when the store
/// did not register anything new (a lost race), so the number of rows one issuer can create per window is bounded. When the budget is
/// exhausted the call returns <see cref="TenantRegistration.RateLimited"/> without writing. The number of tracked issuers is bounded; further
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

        // Phase 5 L6: existence before budget. A lookup failure is an unavailable registry and fails closed (it throws).
        var existing = await inner.TryGetAsync(tenant, ct).ConfigureAwait(false);
        if (existing == TenantRegistration.Existing)
        {
            Remember(tenant, now);
            return TenantRegistration.Existing;
        }

        if (existing == TenantRegistration.Collision)
        {
            EvictGroup(TenantRegistryKey.Normalize(tenant.Value));
            return TenantRegistration.Collision;
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

        if (result is TenantRegistration.Registered or TenantRegistration.Existing)
        {
            Remember(tenant, now);
        }
        else if (result == TenantRegistration.Collision)
        {
            EvictGroup(TenantRegistryKey.Normalize(tenant.Value));
        }

        return result;
    }

    private void Remember(TenantId tenant, DateTimeOffset now)
    {
        if (_cache.Count < options.MaxCacheEntries)
        {
            _cache[tenant.Value] = new CacheEntry(TenantRegistryKey.Normalize(tenant.Value), now + _ttl);
        }
    }

    private void EvictGroup(string key)
    {
        foreach (var pair in _cache)
        {
            if (pair.Value.Key == key)
            {
                _cache.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <summary>M-A2: a read-only lookup (never registers, never costs budget). Only a positive answer is remembered.</summary>
    public async ValueTask<TenantRegistration?> TryGetAsync(TenantId tenant, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_cache.TryGetValue(tenant.Value, out var hit) && hit.ExpiresAt > now)
        {
            return TenantRegistration.Existing;
        }

        var result = await inner.TryGetAsync(tenant, ct).ConfigureAwait(false);
        if (result == TenantRegistration.Existing)
        {
            Remember(tenant, now);
        }
        else if (result == TenantRegistration.Collision)
        {
            EvictGroup(TenantRegistryKey.Normalize(tenant.Value));
        }

        return result;
    }

    public async ValueTask<bool> ResolveCollisionAsync(TenantId keep, CancellationToken ct)
    {
        var resolved = await inner.ResolveCollisionAsync(keep, ct).ConfigureAwait(false);
        EvictGroup(TenantRegistryKey.Normalize(keep.Value));
        return resolved;
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
