using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Options;

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
    private readonly TimeSpan _window;
    private readonly TimeSpan _successLifetime;
    private readonly TimeProvider _timeProvider;

    private sealed class FailureState
    {
        public int Count;
        public DateTimeOffset WindowStart;
        public DateTimeOffset? LockedUntil;
    }

    private sealed record SuccessEntry(string Username, DateTimeOffset ExpiresAt);

    internal BasicAuthAttemptGuard(BasicAuthOptions options, TimeProvider? timeProvider = null)
    {
        _maxFailedAttempts = Math.Max(1, options.MaxFailedAttempts);
        _window = TimeSpan.FromSeconds(Math.Max(1, options.FailureWindowSeconds));
        _successLifetime = TimeSpan.FromSeconds(Math.Clamp(options.SuccessCacheSeconds, 0, 300));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static BasicAuthAttemptGuard For(BasicAuthOptions options) =>
        Guards.GetValue(options, static o => new BasicAuthAttemptGuard(o));

    public static string BuildAttemptKey(string username, string? clientIp) =>
        username.ToUpperInvariant() + "|" + (string.IsNullOrWhiteSpace(clientIp) ? "unknown" : clientIp);

    public bool IsLockedOut(string attemptKey)
    {
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

    public void RecordFailure(string attemptKey)
    {
        var now = _timeProvider.GetUtcNow();
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
            if (state.Count >= _maxFailedAttempts)
            {
                state.LockedUntil = now + _window;
            }
        }
    }

    public void RecordSuccess(string attemptKey) => _failures.TryRemove(attemptKey, out _);

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
