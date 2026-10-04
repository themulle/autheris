using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Autheris.Infrastructure.Idempotency;

public sealed class RedisIdempotencyStore : IIdempotencyStore
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly ILogger<RedisIdempotencyStore> _logger;
    private readonly string _prefix;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public RedisIdempotencyStore(
        IConnectionMultiplexer multiplexer,
        IOptions<GatewayOptions> options,
        ILogger<RedisIdempotencyStore> logger)
    {
        _multiplexer = multiplexer;
        _logger = logger;
        _prefix = options.Value.Caching.Redis.InstanceName;
        if (!_prefix.EndsWith(':'))
        {
            _prefix += ":";
        }
    }

    private static readonly Prometheus.Counter IdempotencyRedisErrors = Prometheus.Metrics.CreateCounter(
        "autheris_idempotency_redis_errors_total", "Number of Redis errors in Idempotency store",
        new Prometheus.CounterConfiguration { LabelNames = ["operation"] });

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class
    {
        try
        {
            var db = _multiplexer.GetDatabase();
            var redisKey = (RedisKey)$"{_prefix}idempotency:{key}";
            var val = await db.StringGetAsync(redisKey).ConfigureAwait(false);
            if (!val.IsNullOrEmpty)
            {
                return JsonSerializer.Deserialize<T>(val.ToString(), _jsonOptions);
            }
        }
        catch (Exception ex)
        {
            IdempotencyRedisErrors.WithLabels("get").Inc();
            _logger.LogError(ex, "Failed to fetch idempotency key {Key} from Redis", key);
        }
        return null;
    }

    public async Task<bool> SetIfNotExistsAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) where T : class
    {
        try
        {
            var db = _multiplexer.GetDatabase();
            var redisKey = (RedisKey)$"{_prefix}idempotency:{key}";
            var json = JsonSerializer.Serialize(value, _jsonOptions);
            return await db.StringSetAsync(redisKey, json, ttl, When.NotExists).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            IdempotencyRedisErrors.WithLabels("set").Inc();
            _logger.LogError(ex, "Failed to set idempotency key {Key} in Redis", key);
            return false;
        }
    }
}
