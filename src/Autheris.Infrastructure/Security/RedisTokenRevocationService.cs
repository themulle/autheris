using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Autheris.Infrastructure.Security;

/// <summary>
/// SEC M-14 (GAP-B): Cluster-wide token revocation store on Redis/Garnet. Key
/// <c>{InstanceName}revoked:{jti|subject}</c> (default <c>Autheris:revoked:...</c>), value = revocation time
/// (Unix milliseconds), Redis expiry = <c>until</c>. Revocations issued on this node are also kept locally, so they
/// stay effective during a Redis outage; revocations of other nodes cannot be seen while Redis is unreachable.
/// </summary>
public sealed class RedisTokenRevocationService : ITokenRevocationService
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly ILogger<RedisTokenRevocationService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly InMemoryTokenRevocationService _local;
    private readonly string _prefix;

    public RedisTokenRevocationService(
        IConnectionMultiplexer multiplexer,
        IOptions<GatewayOptions> options,
        ILogger<RedisTokenRevocationService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _local = new InMemoryTokenRevocationService(_timeProvider);

        var instance = options.Value.Caching.Redis.InstanceName;
        if (string.IsNullOrWhiteSpace(instance))
        {
            instance = "Autheris:";
        }
        else if (!instance.EndsWith(':'))
        {
            instance += ":";
        }

        _prefix = instance + "revoked:";

        try
        {
            var sub = _multiplexer.GetSubscriber();
            sub.Subscribe(RedisChannel.Literal(_prefix + "events:revoked"), (ch, msg) =>
            {
                if (msg.IsNullOrEmpty) return;
                var parts = msg.ToString().Split('|');
                if (parts.Length >= 3 &&
                    long.TryParse(parts[1], out var revokedAtMs) &&
                    long.TryParse(parts[2], out var untilMs))
                {
                    _local.Revoke(parts[0], DateTimeOffset.FromUnixTimeMilliseconds(revokedAtMs), DateTimeOffset.FromUnixTimeMilliseconds(untilMs));
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe to Redis token revocation channel.");
        }
    }

    internal string BuildKey(string normalizedKey) => _prefix + normalizedKey;

    public async ValueTask<bool> IsRevokedAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (_local.IsRevoked(principal))
        {
            return true;
        }

        var keys = TokenRevocationKeys.GetLookupKeys(principal);
        if (keys.Count == 0)
        {
            return false;
        }

        var issuedAt = TokenRevocationKeys.GetIssuedAt(principal);
        try
        {
            var db = _multiplexer.GetDatabase();
            ct.ThrowIfCancellationRequested();

            // One round trip for all lookup keys (jti, SID, sub and their tenant-scoped variants, review E-8).
            var redisKeys = new RedisKey[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                redisKeys[i] = BuildKey(keys[i]);
            }

            var values = await db.StringGetAsync(redisKeys).ConfigureAwait(false);
            foreach (var value in values)
            {
                if (value.IsNullOrEmpty)
                {
                    continue;
                }

                if (!long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var revokedAtMs))
                {
                    // Unreadable entry: treat as revoked (fail-closed for an explicit revocation marker).
                    return true;
                }

                if (TokenRevocationKeys.IsCovered(issuedAt, DateTimeOffset.FromUnixTimeMilliseconds(revokedAtMs)))
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token revocation lookup in Redis failed; only locally known revocations are enforced.");
        }

        return false;
    }

    public async Task RevokeAsync(string subjectOrJti, DateTimeOffset until, CancellationToken ct = default)
    {
        var key = TokenRevocationKeys.Normalize(subjectOrJti);
        var now = _timeProvider.GetUtcNow();
        _local.Revoke(key, now, until);

        var ttl = until - now;
        if (ttl <= TimeSpan.Zero)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        var db = _multiplexer.GetDatabase();
        var value = now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        await db.StringSetAsync((RedisKey)BuildKey(key), value, ttl, When.Always).ConfigureAwait(false);

        try
        {
            var sub = _multiplexer.GetSubscriber();
            var payload = $"{key}|{now.ToUnixTimeMilliseconds()}|{until.ToUnixTimeMilliseconds()}";
            await sub.PublishAsync(RedisChannel.Literal(_prefix + "events:revoked"), (RedisValue)payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish token revocation event to Redis cluster.");
        }
    }
}
