using Autheris.Application.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics.Metrics;
using StackExchange.Redis;

namespace Autheris.Infrastructure.RateLimiting;

public sealed class RedisRateLimiterService : IRateLimiterService
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly IDatabase _db;
    private readonly ILogger<RedisRateLimiterService> _logger;
    private readonly string _prefix;

    private const string PreAuthIpScript = @"
        local current = redis.call('INCR', KEYS[1])
        if current == 1 then
            redis.call('EXPIRE', KEYS[1], ARGV[1])
        end
        local ttl = redis.call('TTL', KEYS[1])
        return { current, ttl }
    ";

    private const string PostAuthSidScript = @"
        local key = KEYS[1]
        local capacity = tonumber(ARGV[1])
        local refill_rate = tonumber(ARGV[2])
        local now_ms = tonumber(ARGV[3])
        local ttl_seconds = tonumber(ARGV[4])

        local data = redis.call('HMGET', key, 'tokens', 'last_refill')
        local tokens = tonumber(data[1])
        local last_refill = tonumber(data[2])

        if not tokens then
            tokens = capacity
            last_refill = now_ms
        else
            local elapsed_sec = math.max(0, (now_ms - last_refill) / 1000.0)
            tokens = math.min(capacity, tokens + (elapsed_sec * refill_rate))
            last_refill = now_ms
        end

        local allowed = 0
        local wait_seconds = 1
        if tokens >= 1.0 then
            tokens = tokens - 1.0
            allowed = 1
        else
            local missing = 1.0 - tokens
            local rate = refill_rate > 0 and refill_rate or 1
            wait_seconds = math.max(1, math.ceil(missing / rate))
        end

        redis.call('HSET', key, 'tokens', tostring(tokens), 'last_refill', tostring(last_refill))
        redis.call('EXPIRE', key, ttl_seconds)
        return { allowed, wait_seconds }
    ";

    private const string CostTokenBucketScript = @"
        local key = KEYS[1]
        local requested_cost = tonumber(ARGV[1])
        local capacity = tonumber(ARGV[2])
        local refill_rate = tonumber(ARGV[3])
        local now_ms = tonumber(ARGV[4])
        local ttl_seconds = tonumber(ARGV[5])

        local data = redis.call('HMGET', key, 'tokens', 'last_refill')
        local tokens = tonumber(data[1])
        local last_refill = tonumber(data[2])

        if not tokens then
            tokens = capacity
            last_refill = now_ms
        else
            local elapsed_sec = math.max(0, (now_ms - last_refill) / 1000.0)
            tokens = math.min(capacity, tokens + (elapsed_sec * refill_rate))
            last_refill = math.max(last_refill, now_ms)
        end

        local allowed = 0
        local wait_seconds = 0

        if tokens >= requested_cost then
            tokens = tokens - requested_cost
            allowed = 1
        else
            local missing = requested_cost - tokens
            local rate = refill_rate > 0 and refill_rate or 1.0
            wait_seconds = math.max(1, math.ceil(missing / rate))
        end

        redis.call('HSET', key, 'tokens', tostring(tokens), 'last_refill', tostring(last_refill))
        redis.call('EXPIRE', key, ttl_seconds)

        return { allowed, math.floor(tokens), wait_seconds }
    ";

    private const string CostTokenBucketSettleScript = @"
        local key = KEYS[1]
        local reserved_cost = tonumber(ARGV[1])
        local actual_cost = tonumber(ARGV[2])
        local capacity = tonumber(ARGV[3])
        local refill_rate = tonumber(ARGV[4])
        local now_ms = tonumber(ARGV[5])
        local ttl_seconds = tonumber(ARGV[6])

        local data = redis.call('HMGET', key, 'tokens', 'last_refill')
        local tokens = tonumber(data[1])
        local last_refill = tonumber(data[2])

        if not tokens then
            tokens = capacity
            last_refill = now_ms
        else
            local elapsed_sec = math.max(0, (now_ms - last_refill) / 1000.0)
            tokens = math.min(capacity, tokens + (elapsed_sec * refill_rate))
            last_refill = math.max(last_refill, now_ms)
        end

        local delta = reserved_cost - actual_cost
        tokens = math.max(0, math.min(capacity, tokens + delta))

        redis.call('HSET', key, 'tokens', tostring(tokens), 'last_refill', tostring(last_refill))
        redis.call('EXPIRE', key, ttl_seconds)

        return { 1, math.floor(tokens), 0 }
    ";

    private static readonly LuaScript PreparedPreAuthIpScript = LuaScript.Prepare(PreAuthIpScript);
    private static readonly LuaScript PreparedPostAuthSidScript = LuaScript.Prepare(PostAuthSidScript);
    private static readonly LuaScript PreparedCostTokenBucketScript = LuaScript.Prepare(CostTokenBucketScript);
    private static readonly LuaScript PreparedCostTokenBucketSettleScript = LuaScript.Prepare(CostTokenBucketSettleScript);

    private readonly TimeProvider _timeProvider;

    public RedisRateLimiterService(
        IConnectionMultiplexer multiplexer,
        IOptions<GatewayOptions> options,
        ILogger<RedisRateLimiterService> logger,
        TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _multiplexer = multiplexer;
        _db = multiplexer.GetDatabase();
        _logger = logger;
        _prefix = options.Value.Caching.Redis.InstanceName;
        if (!_prefix.EndsWith(':'))
        {
            _prefix += ":";
        }
    }

    private static readonly Meter Meter = new("Autheris.Core");
    private static readonly Counter<long> RedisFailures = Meter.CreateCounter<long>(
        "autheris.redis.failures", description: "Number of Redis errors");

    private static void RecordRedisFailure(string operation) =>
        RedisFailures.Add(1,
            new KeyValuePair<string, object?>("autheris.redis.component", "ratelimiter"),
            new KeyValuePair<string, object?>("autheris.redis.operation", operation));

    private readonly InMemoryRateLimiterService _inMemoryFallback = new();

    public async Task<RateLimitResult> CheckPreAuthIpAsync(string ip, PreAuthIpRateLimitOptions options, CancellationToken ct = default)
    {
        try
        {
            var sanitizedIp = ip.Replace("{", "_").Replace("}", "_");
            var key = (RedisKey)$"{_prefix}ratelimit:ip:{sanitizedIp}";
            var res = (RedisResult[]?)await PreparedPreAuthIpScript.EvaluateAsync(
                _db,
                new { KEYS = new RedisKey[] { key }, ARGV = new RedisValue[] { (RedisValue)options.WindowSeconds } }
            ).ConfigureAwait(false);

            if (res != null && res.Length >= 2)
            {
                var count = (long)res[0];
                var ttl = (long)res[1];
                var retryAfter = Math.Max(1, (int)ttl);

                return new RateLimitResult(count <= options.PermitLimit, retryAfter);
            }

            RecordRedisFailure("pre_auth_ip");
            _logger.LogWarning("Redis rate limiting returned unexpected result for IP {Ip}. Falling back to in-memory limiter.", ip);
            return await _inMemoryFallback.CheckPreAuthIpAsync(ip, options, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordRedisFailure("pre_auth_ip");
            _logger.LogWarning(ex, "Redis rate limiting failed for IP {Ip}. Falling back to local in-memory rate limiter with conservative degraded budget.", ip);
            try
            {
                var degradedOptions = new PreAuthIpRateLimitOptions
                {
                    PermitLimit = Math.Max(1, options.PermitLimit / 2),
                    WindowSeconds = options.WindowSeconds,
                    QueueLimit = options.QueueLimit
                };
                return await _inMemoryFallback.CheckPreAuthIpAsync(ip, degradedOptions, ct).ConfigureAwait(false);
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "In-memory rate limiting fallback failed for IP {Ip}. Failing closed.", ip);
                return new RateLimitResult(false, 60);
            }
        }
    }

    public async Task<RateLimitResult> CheckPostAuthSidAsync(string sid, PostAuthSidRateLimitOptions options, CancellationToken ct = default)
    {
        try
        {
            var sanitizedSid = sid.Replace("{", "_").Replace("}", "_");
            var key = (RedisKey)$"{_prefix}ratelimit:sid:{sanitizedSid}";
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ttlSeconds = 600; // 10 minutes idle expiry

            var res = (RedisResult[]?)await PreparedPostAuthSidScript.EvaluateAsync(
                _db,
                new
                {
                    KEYS = new RedisKey[] { key },
                    ARGV = new RedisValue[]
                    {
                        (RedisValue)options.TokenBucketCapacity,
                        (RedisValue)options.TokensPerSecond,
                        (RedisValue)nowMs,
                        (RedisValue)ttlSeconds
                    }
                }
            ).ConfigureAwait(false);

            if (res != null && res.Length >= 2)
            {
                var allowed = (long)res[0] == 1;
                var waitSeconds = Math.Max(1, (int)(long)res[1]);

                return new RateLimitResult(allowed, waitSeconds);
            }

            RecordRedisFailure("post_auth_sid");
            _logger.LogWarning("Redis post-auth rate limiting returned unexpected result for SID {Sid}. Falling back to in-memory limiter.", sid);
            var degradedFallback = new PostAuthSidRateLimitOptions
            {
                TokenBucketCapacity = Math.Max(1, options.TokenBucketCapacity / 2),
                TokensPerSecond = Math.Max(1, options.TokensPerSecond / 2)
            };
            return await _inMemoryFallback.CheckPostAuthSidAsync(sid, degradedFallback, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordRedisFailure("post_auth_sid");
            _logger.LogWarning(ex, "Redis post-auth rate limiting failed for SID {Sid}. Falling back to local in-memory token bucket with conservative degraded budget.", sid);
            try
            {
                var degradedOptions = new PostAuthSidRateLimitOptions
                {
                    TokenBucketCapacity = Math.Max(1, options.TokenBucketCapacity / 2),
                    TokensPerSecond = Math.Max(1, options.TokensPerSecond / 2)
                };
                return await _inMemoryFallback.CheckPostAuthSidAsync(sid, degradedOptions, ct).ConfigureAwait(false);
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "In-memory rate limiting fallback failed for SID {Sid}. Failing closed.", sid);
                return new RateLimitResult(false, 60);
            }
        }
    }

    public async Task<CostQuotaResult> CheckCostQuotaAsync(string key, int requestedCost, ClientQuotaPolicy policy, CancellationToken ct = default)
    {
        try
        {
            var sanitizedKey = key.Replace("{", "_").Replace("}", "_");
            var redisKey = (RedisKey)$"{_prefix}ratelimit:quota:{sanitizedKey}";
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ttlSeconds = 600;

            var res = (RedisResult[]?)await PreparedCostTokenBucketScript.EvaluateAsync(
                _db,
                new
                {
                    KEYS = new RedisKey[] { redisKey },
                    ARGV = new RedisValue[]
                    {
                        (RedisValue)requestedCost,
                        (RedisValue)policy.MaxTokensCapacity,
                        (RedisValue)policy.TokenRefillRatePerSecond,
                        (RedisValue)nowMs,
                        (RedisValue)ttlSeconds
                    }
                }
            ).ConfigureAwait(false);

            if (res != null && res.Length >= 3)
            {
                var allowed = (long)res[0] == 1;
                var remaining = (int)(long)res[1];
                var waitSeconds = (int)(long)res[2];
                return new CostQuotaResult(allowed, remaining, waitSeconds);
            }

            RecordRedisFailure("cost_quota");
            _logger.LogWarning("Redis cost quota returned unexpected result for key {Key}. Falling back to in-memory limiter.", key);
            return await _inMemoryFallback.CheckCostQuotaAsync(key, requestedCost, policy, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordRedisFailure("cost_quota");
            _logger.LogWarning(ex, "Redis cost quota failed for key {Key}. Falling back to in-memory limiter.", key);
            try
            {
                return await _inMemoryFallback.CheckCostQuotaAsync(key, requestedCost, policy, ct).ConfigureAwait(false);
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "In-memory rate limiting fallback failed for key {Key}. Failing closed.", key);
                return new CostQuotaResult(false, 0, 60);
            }
        }
    }

    public Task<CostQuotaResult> ReserveCostQuotaAsync(string key, int reservedCost, ClientQuotaPolicy policy, CancellationToken ct = default)
        => CheckCostQuotaAsync(key, reservedCost, policy, ct);

    public async Task<CostQuotaResult> SettleCostQuotaAsync(string key, int reservedCost, int actualCost, ClientQuotaPolicy policy, CancellationToken ct = default)
    {
        if (reservedCost < 0) reservedCost = 0;
        if (actualCost < 0) actualCost = 0;

        try
        {
            var sanitizedKey = key.Replace("{", "_").Replace("}", "_");
            var redisKey = (RedisKey)$"{_prefix}ratelimit:quota:{sanitizedKey}";
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ttlSeconds = 600;

            var res = (RedisResult[]?)await PreparedCostTokenBucketSettleScript.EvaluateAsync(
                _db,
                new
                {
                    KEYS = new RedisKey[] { redisKey },
                    ARGV = new RedisValue[]
                    {
                        (RedisValue)reservedCost,
                        (RedisValue)actualCost,
                        (RedisValue)policy.MaxTokensCapacity,
                        (RedisValue)policy.TokenRefillRatePerSecond,
                        (RedisValue)nowMs,
                        (RedisValue)ttlSeconds
                    }
                }
            ).ConfigureAwait(false);

            if (res != null && res.Length >= 3)
            {
                var allowed = (long)res[0] == 1;
                var remaining = (int)(long)res[1];
                var waitSeconds = (int)(long)res[2];
                return new CostQuotaResult(allowed, remaining, waitSeconds);
            }

            RecordRedisFailure("cost_quota_settle");
            _logger.LogWarning("Redis cost quota settlement returned unexpected result for key {Key}. Falling back to in-memory limiter.", key);
            return await _inMemoryFallback.SettleCostQuotaAsync(key, reservedCost, actualCost, policy, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordRedisFailure("cost_quota_settle");
            _logger.LogWarning(ex, "Redis cost quota settlement failed for key {Key}. Falling back to in-memory limiter.", key);
            try
            {
                return await _inMemoryFallback.SettleCostQuotaAsync(key, reservedCost, actualCost, policy, ct).ConfigureAwait(false);
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "In-memory rate limiting fallback failed for key {Key}. Failing closed.", key);
                return new CostQuotaResult(true, 0, 0);
            }
        }
    }

    /// <summary>
    /// SEC-GOV175B-01 &amp; SEC-GOV175B-03: Non-destructive probe of Redis bucket state via read-only HMGET.
    /// </summary>
    public async Task<CallerBucketState?> GetBucketStateAsync(string key, ClientQuotaPolicy policy, CancellationToken ct = default)
    {
        var capacity = policy.MaxTokensCapacity;
        var refillRate = Math.Max(0.0, policy.TokenRefillRatePerSecond);

        try
        {
            var sanitizedKey = key.Replace("{", "_").Replace("}", "_");
            var redisKey = (RedisKey)$"{_prefix}ratelimit:quota:{sanitizedKey}";

            var data = await _db.HashGetAsync(redisKey, [(RedisValue)"tokens", (RedisValue)"last_refill"]).ConfigureAwait(false);

            if (data == null || data.Length < 2 || data[0].IsNullOrEmpty || data[1].IsNullOrEmpty)
            {
                // Uninitialized bucket: uniform full capacity
                return new CallerBucketState(capacity, capacity, refillRate);
            }

            if (double.TryParse((string?)data[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tokens) &&
                long.TryParse((string?)data[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var lastRefillMs))
            {
                var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                var elapsedSec = Math.Max(0.0, (nowMs - lastRefillMs) / 1000.0);
                var currentTokens = Math.Min(capacity, tokens + (elapsedSec * refillRate));
                return new CallerBucketState(capacity, Math.Round(currentTokens, 2), refillRate);
            }

            return new CallerBucketState(capacity, capacity, refillRate);
        }
        catch (Exception ex)
        {
            RecordRedisFailure("cost_quota");
            _logger.LogWarning(ex, "Redis cost quota probe failed for key {Key}. Falling back to in-memory probe.", key);
            try
            {
                return await _inMemoryFallback.GetBucketStateAsync(key, policy, ct).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }
    }

    public Task<CallerBucketState?> GetBucketStateAsync(string key, CancellationToken ct = default)
    {
        return GetBucketStateAsync(key, ClientQuotaPolicy.ForTier(ClientTier.Standard), ct);
    }
}
