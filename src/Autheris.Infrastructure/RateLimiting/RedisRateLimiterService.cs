using Autheris.Application.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
            last_refill = now_ms
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

    private static readonly LuaScript PreparedPreAuthIpScript = LuaScript.Prepare(PreAuthIpScript);
    private static readonly LuaScript PreparedPostAuthSidScript = LuaScript.Prepare(PostAuthSidScript);
    private static readonly LuaScript PreparedCostTokenBucketScript = LuaScript.Prepare(CostTokenBucketScript);

    public RedisRateLimiterService(
        IConnectionMultiplexer multiplexer,
        IOptions<GatewayOptions> options,
        ILogger<RedisRateLimiterService> logger)
    {
        _multiplexer = multiplexer;
        _db = multiplexer.GetDatabase();
        _logger = logger;
        _prefix = options.Value.Caching.Redis.InstanceName;
        if (!_prefix.EndsWith(':'))
        {
            _prefix += ":";
        }
    }

    private static readonly Prometheus.Counter RateLimiterRedisErrors = Prometheus.Metrics.CreateCounter(
        "autheris_ratelimiter_redis_errors_total", "Number of Redis errors in Rate Limiter service",
        new Prometheus.CounterConfiguration { LabelNames = ["limit_type"] });

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

            RateLimiterRedisErrors.WithLabels("pre_auth_ip").Inc();
            _logger.LogWarning("Redis rate limiting returned unexpected result for IP {Ip}. Falling back to in-memory limiter.", ip);
            return await _inMemoryFallback.CheckPreAuthIpAsync(ip, options, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RateLimiterRedisErrors.WithLabels("pre_auth_ip").Inc();
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

            RateLimiterRedisErrors.WithLabels("post_auth_sid").Inc();
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
            RateLimiterRedisErrors.WithLabels("post_auth_sid").Inc();
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

            RateLimiterRedisErrors.WithLabels("cost_quota").Inc();
            _logger.LogWarning("Redis cost quota returned unexpected result for key {Key}. Falling back to in-memory limiter.", key);
            return await _inMemoryFallback.CheckCostQuotaAsync(key, requestedCost, policy, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RateLimiterRedisErrors.WithLabels("cost_quota").Inc();
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
}
