using System.Collections.Concurrent;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;

namespace Autheris.Infrastructure.RateLimiting;

public sealed class InMemoryRateLimiterService : IRateLimiterService
{
    private sealed class IpCounter
    {
        public int Count;
        public long WindowStartTimestamp;
        public readonly object Lock = new();
    }

    private sealed class TokenBucket
    {
        public double Tokens;
        public long LastRefillTimestamp;
        public readonly object Lock = new();
    }

    private readonly ConcurrentDictionary<string, IpCounter> _ipCounters = new();
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new(StringComparer.OrdinalIgnoreCase);

    private int _ipCount;
    private int _bucketCount;

    private long _lastIpCleanup;
    private int _isCleaningUpIp;

    private long _lastBucketCleanup;
    private int _isCleaningUpBuckets;

    private readonly CancellationToken _stoppingToken;

    // SEC M-08: Upper bound per map. When reached, the stalest entries are evicted (LRU-like) instead of
    // rejecting every new client with 429.
    public const int MaxEntries = 25000;
    public const int EvictionBatchSize = MaxEntries / 10;
    private int _isEvictingIp;
    private int _isEvictingBuckets;
    private int _isEvictingCost;

    private readonly TimeProvider _timeProvider;

    public InMemoryRateLimiterService(IHostApplicationLifetime? lifetime = null, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        var now = _timeProvider.GetTimestamp();
        _lastIpCleanup = now;
        _lastBucketCleanup = now;
        _lastCostCleanup = now;
        _stoppingToken = lifetime?.ApplicationStopping ?? CancellationToken.None;
    }

    public Task<RateLimitResult> CheckPreAuthIpAsync(string ip, PreAuthIpRateLimitOptions options, CancellationToken ct = default)
    {
        var currentTimestamp = _timeProvider.GetTimestamp();
        var windowDuration = TimeSpan.FromSeconds(options.WindowSeconds);

        if (Volatile.Read(ref _ipCount) > 5000 &&
            _timeProvider.GetElapsedTime(_lastIpCleanup, currentTimestamp) > TimeSpan.FromSeconds(60) &&
            Interlocked.CompareExchange(ref _isCleaningUpIp, 1, 0) == 0)
        {
            _lastIpCleanup = currentTimestamp;
            _ = Task.Run(() =>
            {
                try
                {
                    var staleThreshold = windowDuration * 2;
                    foreach (var kvp in _ipCounters)
                    {
                        if (_stoppingToken.IsCancellationRequested) break;
                        long ws;
                        lock (kvp.Value.Lock)
                        {
                            ws = kvp.Value.WindowStartTimestamp;
                        }
                        if (_timeProvider.GetElapsedTime(ws, _timeProvider.GetTimestamp()) > staleThreshold)
                        {
                            if (_ipCounters.TryRemove(kvp.Key, out _))
                            {
                                Interlocked.Decrement(ref _ipCount);
                            }
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _isCleaningUpIp, 0);
                }
            }, _stoppingToken);
        }

        ip = ClientIpRateLimitKey.Normalize(ip);

        if (!_ipCounters.TryGetValue(ip, out var counter))
        {
            if (Volatile.Read(ref _ipCount) >= MaxEntries)
            {
                EvictOldest(_ipCounters, c => { lock (c.Lock) { return c.WindowStartTimestamp; } }, ref _ipCount, ref _isEvictingIp);
            }

            var newCounter = new IpCounter { WindowStartTimestamp = currentTimestamp };
            if (_ipCounters.TryAdd(ip, newCounter))
            {
                Interlocked.Increment(ref _ipCount);
                counter = newCounter;
            }
            else
            {
                counter = _ipCounters[ip];
            }
        }
        bool rateLimitExceeded = false;
        int retryAfterSeconds = options.WindowSeconds;

        lock (counter.Lock)
        {
            var elapsed = _timeProvider.GetElapsedTime(counter.WindowStartTimestamp, currentTimestamp);
            if (elapsed > windowDuration)
            {
                counter.WindowStartTimestamp = currentTimestamp;
                counter.Count = 1;
            }
            else
            {
                counter.Count++;
                if (counter.Count > options.PermitLimit)
                {
                    rateLimitExceeded = true;
                    var remaining = windowDuration - elapsed;
                    retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                }
            }
        }

        return Task.FromResult(new RateLimitResult(!rateLimitExceeded, retryAfterSeconds));
    }

    public Task<RateLimitResult> CheckPostAuthSidAsync(string sid, PostAuthSidRateLimitOptions options, CancellationToken ct = default)
    {
        var currentTimestamp = _timeProvider.GetTimestamp();

        if (Volatile.Read(ref _bucketCount) > 5000 &&
            _timeProvider.GetElapsedTime(_lastBucketCleanup, currentTimestamp) > TimeSpan.FromSeconds(60) &&
            Interlocked.CompareExchange(ref _isCleaningUpBuckets, 1, 0) == 0)
        {
            _lastBucketCleanup = currentTimestamp;
            _ = Task.Run(() =>
            {
                try
                {
                    var staleThreshold = TimeSpan.FromMinutes(10);
                    foreach (var kvp in _buckets)
                    {
                        if (_stoppingToken.IsCancellationRequested) break;
                        long lastRefill;
                        lock (kvp.Value.Lock)
                        {
                            lastRefill = kvp.Value.LastRefillTimestamp;
                        }
                        if (_timeProvider.GetElapsedTime(lastRefill, _timeProvider.GetTimestamp()) > staleThreshold)
                        {
                            if (_buckets.TryRemove(kvp.Key, out _))
                            {
                                Interlocked.Decrement(ref _bucketCount);
                            }
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _isCleaningUpBuckets, 0);
                }
            }, _stoppingToken);
        }

        if (!_buckets.TryGetValue(sid, out var bucket))
        {
            if (Volatile.Read(ref _bucketCount) >= MaxEntries)
            {
                EvictOldest(_buckets, b => { lock (b.Lock) { return b.LastRefillTimestamp; } }, ref _bucketCount, ref _isEvictingBuckets);
            }

            var newBucket = new TokenBucket
            {
                Tokens = options.TokenBucketCapacity,
                LastRefillTimestamp = currentTimestamp
            };

            if (_buckets.TryAdd(sid, newBucket))
            {
                Interlocked.Increment(ref _bucketCount);
                bucket = newBucket;
            }
            else
            {
                bucket = _buckets[sid];
            }
        }

        bool limitExceeded = false;
        int waitSeconds = 1;

        lock (bucket.Lock)
        {
            var elapsedSeconds = _timeProvider.GetElapsedTime(bucket.LastRefillTimestamp, currentTimestamp).TotalSeconds;
            bucket.Tokens = Math.Min(options.TokenBucketCapacity, bucket.Tokens + (elapsedSeconds * options.TokensPerSecond));
            bucket.LastRefillTimestamp = currentTimestamp;

            if (bucket.Tokens >= 1.0)
            {
                bucket.Tokens -= 1.0;
            }
            else
            {
                limitExceeded = true;
                var missingTokens = 1.0 - bucket.Tokens;
                var refillRate = options.TokensPerSecond > 0 ? options.TokensPerSecond : 1;
                waitSeconds = Math.Max(1, (int)Math.Ceiling(missingTokens / refillRate));
            }
        }

        return Task.FromResult(new RateLimitResult(!limitExceeded, waitSeconds));
    }

    /// <summary>Number of tracked pre-auth IP keys (for diagnostics/tests).</summary>
    public int TrackedIpCount => Volatile.Read(ref _ipCount);

    private static void EvictOldest<T>(
        ConcurrentDictionary<string, T> map,
        Func<T, long> timestampSelector,
        ref int count,
        ref int evictionFlag)
        where T : class
    {
        if (Interlocked.CompareExchange(ref evictionFlag, 1, 0) != 0)
        {
            // Another thread is already evicting; admit the new client rather than blocking it.
            return;
        }

        try
        {
            var snapshot = new List<KeyValuePair<string, long>>(map.Count);
            foreach (var kvp in map)
            {
                snapshot.Add(new KeyValuePair<string, long>(kvp.Key, timestampSelector(kvp.Value)));
            }

            snapshot.Sort(static (a, b) => a.Value.CompareTo(b.Value));

            var toRemove = Math.Min(EvictionBatchSize, snapshot.Count);
            for (var i = 0; i < toRemove; i++)
            {
                if (map.TryRemove(snapshot[i].Key, out _))
                {
                    Interlocked.Decrement(ref count);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref evictionFlag, 0);
        }
    }

    private readonly ConcurrentDictionary<string, TokenBucket> _costBuckets = new(StringComparer.OrdinalIgnoreCase);
    private int _costBucketCount;
    private long _lastCostCleanup;
    private int _isCleaningUpCost;
    private const int MaxCostBuckets = 25000;

    public Task<CostQuotaResult> CheckCostQuotaAsync(string key, int requestedCost, ClientQuotaPolicy policy, CancellationToken ct = default)
    {
        var currentTimestamp = _timeProvider.GetTimestamp();

        if (Volatile.Read(ref _costBucketCount) > 5000 &&
            _timeProvider.GetElapsedTime(_lastCostCleanup, currentTimestamp) > TimeSpan.FromSeconds(60) &&
            Interlocked.CompareExchange(ref _isCleaningUpCost, 1, 0) == 0)
        {
            _lastCostCleanup = currentTimestamp;
            _ = Task.Run(() =>
            {
                try
                {
                    var staleThreshold = TimeSpan.FromMinutes(10);
                    foreach (var kvp in _costBuckets)
                    {
                        if (_stoppingToken.IsCancellationRequested) break;
                        long lastRefill;
                        lock (kvp.Value.Lock)
                        {
                            lastRefill = kvp.Value.LastRefillTimestamp;
                        }
                        if (_timeProvider.GetElapsedTime(lastRefill, _timeProvider.GetTimestamp()) > staleThreshold)
                        {
                            if (_costBuckets.TryRemove(kvp.Key, out _))
                            {
                                Interlocked.Decrement(ref _costBucketCount);
                            }
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _isCleaningUpCost, 0);
                }
            }, _stoppingToken);
        }

        if (!_costBuckets.TryGetValue(key, out var bucket))
        {
            if (Volatile.Read(ref _costBucketCount) >= MaxCostBuckets)
            {
                EvictOldest(_costBuckets, b => { lock (b.Lock) { return b.LastRefillTimestamp; } }, ref _costBucketCount, ref _isEvictingCost);
            }

            bucket = _costBuckets.GetOrAdd(key, _ =>
            {
                Interlocked.Increment(ref _costBucketCount);
                return new TokenBucket
                {
                    Tokens = policy.MaxTokensCapacity,
                    LastRefillTimestamp = currentTimestamp
                };
            });
        }

        lock (bucket.Lock)
        {
            var elapsedSeconds = _timeProvider.GetElapsedTime(bucket.LastRefillTimestamp, currentTimestamp).TotalSeconds;
            bucket.Tokens = Math.Min(policy.MaxTokensCapacity, bucket.Tokens + (elapsedSeconds * policy.TokenRefillRatePerSecond));
            bucket.LastRefillTimestamp = currentTimestamp;

            if (bucket.Tokens >= requestedCost)
            {
                bucket.Tokens -= requestedCost;
                return Task.FromResult(new CostQuotaResult(true, (int)Math.Floor(bucket.Tokens), 0));
            }
            else
            {
                var missingTokens = requestedCost - bucket.Tokens;
                var refillRate = policy.TokenRefillRatePerSecond > 0 ? policy.TokenRefillRatePerSecond : 1.0;
                var waitSeconds = Math.Max(1, (int)Math.Ceiling(missingTokens / refillRate));
                return Task.FromResult(new CostQuotaResult(false, (int)Math.Floor(bucket.Tokens), waitSeconds));
            }
        }
    }

    public Task<CostQuotaResult> ReserveCostQuotaAsync(string key, int reservedCost, ClientQuotaPolicy policy, CancellationToken ct = default)
    {
        return CheckCostQuotaAsync(key, reservedCost, policy, ct);
    }

    public Task<CostQuotaResult> SettleCostQuotaAsync(string key, int reservedCost, int actualCost, ClientQuotaPolicy policy, CancellationToken ct = default)
    {
        if (reservedCost < 0) reservedCost = 0;
        if (actualCost < 0) actualCost = 0;

        var currentTimestamp = _timeProvider.GetTimestamp();

        if (!_costBuckets.TryGetValue(key, out var bucket))
        {
            if (Volatile.Read(ref _costBucketCount) >= MaxCostBuckets)
            {
                EvictOldest(_costBuckets, b => { lock (b.Lock) { return b.LastRefillTimestamp; } }, ref _costBucketCount, ref _isEvictingCost);
            }

            bucket = _costBuckets.GetOrAdd(key, _ =>
            {
                Interlocked.Increment(ref _costBucketCount);
                return new TokenBucket
                {
                    Tokens = policy.MaxTokensCapacity,
                    LastRefillTimestamp = currentTimestamp
                };
            });
        }

        lock (bucket.Lock)
        {
            // SEC-GOV177-04: Monotonic time progression
            if (currentTimestamp > bucket.LastRefillTimestamp)
            {
                var elapsedSeconds = _timeProvider.GetElapsedTime(bucket.LastRefillTimestamp, currentTimestamp).TotalSeconds;
                bucket.Tokens = Math.Min(policy.MaxTokensCapacity, bucket.Tokens + (elapsedSeconds * policy.TokenRefillRatePerSecond));
                bucket.LastRefillTimestamp = currentTimestamp;
            }

            // SEC-GOV177-01 & SEC-GOV177-02: delta = reservedCost - actualCost, clamped to [0, MaxTokensCapacity]
            var delta = reservedCost - actualCost;
            bucket.Tokens = Math.Clamp(bucket.Tokens + delta, 0, policy.MaxTokensCapacity);

            return Task.FromResult(new CostQuotaResult(true, (int)Math.Floor(bucket.Tokens), 0));
        }
    }

    /// <summary>
    /// SEC-GOV175B-01 &amp; SEC-GOV175B-03: Non-destructive probe of token bucket state without mutations or deductions.
    /// </summary>
    public Task<CallerBucketState?> GetBucketStateAsync(string key, ClientQuotaPolicy policy, CancellationToken ct = default)
    {
        var capacity = policy.MaxTokensCapacity;
        // SEC-GOV175B-F1: project with the real policy rate (never the retry-wait fallback), so the probe
        // never reports more tokens than the consuming path would grant.
        var refillRate = Math.Max(0.0, policy.TokenRefillRatePerSecond);
        var currentTimestamp = _timeProvider.GetTimestamp();

        if (!_costBuckets.TryGetValue(key, out var bucket))
        {
            return Task.FromResult<CallerBucketState?>(new CallerBucketState(capacity, capacity, refillRate));
        }

        lock (bucket.Lock)
        {
            var elapsed = _timeProvider.GetElapsedTime(bucket.LastRefillTimestamp, currentTimestamp).TotalSeconds;
            var tokens = Math.Min(capacity, bucket.Tokens + (elapsed * refillRate));
            return Task.FromResult<CallerBucketState?>(new CallerBucketState(capacity, Math.Round(tokens, 2), refillRate));
        }
    }

    public Task<CallerBucketState?> GetBucketStateAsync(string key, CancellationToken ct = default)
    {
        return GetBucketStateAsync(key, ClientQuotaPolicy.ForTier(ClientTier.Standard), ct);
    }
}
