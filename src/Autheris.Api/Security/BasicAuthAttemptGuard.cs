using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Options;
using Microsoft.Extensions.Caching.Distributed;

namespace Autheris.Api.Security;

/// <summary>
/// RR-L2-03: Brute-force throttling per (user, client IP) and a short-lived cache of verified credentials for
/// Basic authentication. State is bound to the <see cref="BasicAuthOptions"/> instance (singleton in production),
/// so every configuration gets its own isolated guard.
/// </summary>
internal sealed class BasicAuthAttemptGuard
{
    private const int MaxTrackedEntries = 10_000;

    private static readonly ConditionalWeakTable<BasicAuthOptions, BasicAuthAttemptGuard> Guards = new();

    private readonly ConcurrentDictionary<string, FailureState> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SuccessEntry> _successes = new(StringComparer.Ordinal);
    private readonly byte[] _cacheKey = RandomNumberGenerator.GetBytes(32);
    private readonly int _maxFailedAttempts;
    private readonly int _maxFailedAttemptsPerIp;
    private readonly TimeSpan _window;
    private readonly TimeSpan _successLifetime;
    private readonly TimeProvider _timeProvider;
    private readonly Microsoft.Extensions.Caching.Distributed.IDistributedCache? _distributedCache;

    private sealed class FailureState
    {
        public int Count;
        public DateTimeOffset WindowStart;
        public DateTimeOffset? LockedUntil;
    }

    private sealed record SuccessEntry(string Username, DateTimeOffset ExpiresAt);

    internal BasicAuthAttemptGuard(
        BasicAuthOptions options,
        TimeProvider? timeProvider = null,
        Microsoft.Extensions.Caching.Distributed.IDistributedCache? distributedCache = null)
    {
        _maxFailedAttempts = Math.Max(1, options.MaxFailedAttempts);
        _maxFailedAttemptsPerIp = Math.Max(_maxFailedAttempts, options.MaxFailedAttemptsPerIp);
        _window = TimeSpan.FromSeconds(Math.Max(1, options.FailureWindowSeconds));
        _successLifetime = TimeSpan.FromSeconds(Math.Clamp(options.SuccessCacheSeconds, 0, 300));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _distributedCache = distributedCache;
    }

    public static BasicAuthAttemptGuard For(
        BasicAuthOptions options,
        Microsoft.Extensions.Caching.Distributed.IDistributedCache? distributedCache = null) =>
        Guards.GetValue(options, o => new BasicAuthAttemptGuard(o, null, distributedCache));

    public static string BuildAttemptKey(string username, string? clientIp) =>
        username.ToUpperInvariant() + "|" + NormalizeIp(clientIp);

    /// <summary>
    /// Review E-13/A-4: key for the failures of one client address over all user names. Spraying one password over many
    /// accounts never trips the per-(user, IP) counter, but does trip this one.
    /// </summary>
    public static string BuildIpKey(string? clientIp) => "*IP*|" + NormalizeIp(clientIp);

    /// <summary>The (higher) failure limit that applies to <see cref="BuildIpKey"/>.</summary>
    public int MaxFailedAttemptsPerIp => _maxFailedAttemptsPerIp;

    /// <summary>
    /// Review A-4: IPv6 clients usually own a whole /64, so addresses are grouped by their /64 prefix; an attacker
    /// cannot rotate through addresses to reset the counter. IPv4-mapped addresses count as IPv4.
    /// </summary>
    internal static string NormalizeIp(string? clientIp)
    {
        if (string.IsNullOrWhiteSpace(clientIp))
        {
            return "unknown";
        }

        if (!System.Net.IPAddress.TryParse(clientIp, out var ip))
        {
            return clientIp;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            return ip.MapToIPv4().ToString();
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant() + "::/64";
        }

        return ip.ToString();
    }

    public bool IsLockedOut(string attemptKey)
    {
        if (_distributedCache != null)
        {
            try
            {
                var val = _distributedCache.GetString("autheris:lockout:" + attemptKey);
                if (!string.IsNullOrEmpty(val))
                {
                    return true;
                }
            }
            catch
            {
                // Fall back to in-memory tracking on cache communication failure (resilient)
            }
        }

        if (!_failures.TryGetValue(attemptKey, out var state))
        {
            return false;
        }

        lock (state)
        {
            var now = _timeProvider.GetUtcNow();
            if (state.LockedUntil is { } until)
            {
                if (now < until)
                {
                    return true;
                }

                _failures.TryRemove(attemptKey, out _);
            }

            return false;
        }
    }

    public void RecordFailure(string attemptKey, int? maxAttempts = null)
    {
        int limit = Math.Max(1, maxAttempts ?? _maxFailedAttempts);
        var now = _timeProvider.GetUtcNow();

        if (_distributedCache != null)
        {
            try
            {
                string key = "autheris:fail:" + attemptKey;
                string? current = _distributedCache.GetString(key);
                int count = int.TryParse(current, out int c) ? c + 1 : 1;
                _distributedCache.SetString(key, count.ToString(), new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = _window
                });

                if (count >= limit)
                {
                    _distributedCache.SetString("autheris:lockout:" + attemptKey, "1", new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = _window
                    });
                }
            }
            catch
            {
                // Fall back to in-memory tracking
            }
        }

        if (_failures.Count >= MaxTrackedEntries)
        {
            PurgeExpired(now);
        }

        var state = _failures.GetOrAdd(attemptKey, _ => new FailureState { WindowStart = now });
        lock (state)
        {
            if (now - state.WindowStart > _window)
            {
                state.Count = 0;
                state.WindowStart = now;
            }

            state.Count++;
            if (state.Count >= limit)
            {
                state.LockedUntil = now + _window;
            }
        }
    }

    public void RecordSuccess(string attemptKey)
    {
        if (_distributedCache != null)
        {
            try
            {
                _distributedCache.Remove("autheris:fail:" + attemptKey);
                _distributedCache.Remove("autheris:lockout:" + attemptKey);
            }
            catch
            {
                // Ignore distributed cache removal failure
            }
        }

        _failures.TryRemove(attemptKey, out _);
    }

    public bool TryGetCachedSuccess(string authorizationHeader, out string username)
    {
        username = string.Empty;
        if (_successLifetime <= TimeSpan.Zero)
        {
            return false;
        }

        var key = HashHeader(authorizationHeader);
        if (_successes.TryGetValue(key, out var entry))
        {
            if (_timeProvider.GetUtcNow() < entry.ExpiresAt)
            {
                username = entry.Username;
                return true;
            }

            _successes.TryRemove(key, out _);
        }

        return false;
    }

    public void CacheSuccess(string authorizationHeader, string username)
    {
        if (_successLifetime <= TimeSpan.Zero)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (_successes.Count >= MaxTrackedEntries)
        {
            foreach (var kv in _successes)
            {
                if (kv.Value.ExpiresAt <= now)
                {
                    _successes.TryRemove(kv.Key, out _);
                }
            }

            if (_successes.Count >= MaxTrackedEntries)
            {
                return;
            }
        }

        _successes[HashHeader(authorizationHeader)] = new SuccessEntry(username, now + _successLifetime);
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        foreach (var kv in _failures)
        {
            var state = kv.Value;
            lock (state)
            {
                var expired = state.LockedUntil is { } until ? now >= until : now - state.WindowStart > _window;
                if (expired)
                {
                    _failures.TryRemove(kv.Key, out _);
                }
            }
        }
    }

    // Keyed HMAC so the cache never stores credentials (or an offline-crackable plain hash) in memory.
    private string HashHeader(string authorizationHeader) =>
        Convert.ToHexString(HMACSHA256.HashData(_cacheKey, Encoding.UTF8.GetBytes(authorizationHeader)));
}
