namespace Autheris.Infrastructure.RateLimiting;

using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Domain.Common;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

/// <summary>
/// Shared <see cref="IWriteBudgetStore"/> backed by either <see cref="IDistributedCache"/> or Redis <see cref="IConnectionMultiplexer"/>.
/// Fails closed on backing-store errors when Redis is used.
/// </summary>
public sealed class DistributedCacheWriteBudgetStore : IWriteBudgetStore
{
    private const int StripeCount = 64;

    private static readonly LuaScript AcquireStatementLua = LuaScript.Prepare(@"
        local stmtKey = KEYS[1]
        local rowsKey = KEYS[2]
        local maxStmts = tonumber(ARGV[1])
        local maxRows = tonumber(ARGV[2])
        local stmtTtl = tonumber(ARGV[3])

        local usedRows = tonumber(redis.call('GET', rowsKey) or '0')
        if usedRows >= maxRows then
            return { 0, 0 }
        end

        local currentStmts = tonumber(redis.call('GET', stmtKey) or '0')
        if currentStmts >= maxStmts then
            return { 0, maxRows - usedRows }
        end

        local nextStmts = redis.call('INCR', stmtKey)
        if nextStmts == 1 then
            redis.call('EXPIRE', stmtKey, stmtTtl)
        end

        return { 1, maxRows - usedRows }
    ");

    private static readonly LuaScript RecordRowsLua = LuaScript.Prepare(@"
        local rowsKey = KEYS[1]
        local affected = tonumber(ARGV[1])
        local maxRows = tonumber(ARGV[2])
        local rowsTtl = tonumber(ARGV[3])

        local current = tonumber(redis.call('GET', rowsKey) or '0')
        if (current + affected) > maxRows then
            return 0
        end

        local updated = redis.call('INCRBY', rowsKey, affected)
        if updated == affected then
            redis.call('EXPIRE', rowsKey, rowsTtl)
        elseif redis.call('TTL', rowsKey) < 0 then
            redis.call('EXPIRE', rowsKey, rowsTtl)
        end

        return 1
    ");

    private readonly IDistributedCache? _distributedCache;
    private readonly IDatabase? _redisDb;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim[]? _stripes;
    private readonly string _keyPrefix;

    public DistributedCacheWriteBudgetStore(
        IDistributedCache distributedCache,
        TimeProvider? timeProvider = null,
        string keyPrefix = "autheris:wb:")
    {
        _distributedCache = distributedCache ?? throw new ArgumentNullException(nameof(distributedCache));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _keyPrefix = keyPrefix ?? "autheris:wb:";
        _stripes = CreateStripes();
    }

    public DistributedCacheWriteBudgetStore(
        IConnectionMultiplexer multiplexer,
        TimeProvider? timeProvider = null,
        string keyPrefix = "autheris:wb:")
    {
        ArgumentNullException.ThrowIfNull(multiplexer);
        _redisDb = multiplexer.GetDatabase();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _keyPrefix = keyPrefix ?? "autheris:wb:";
    }

    public async Task<WriteBudgetAcquisition> TryAcquireStatementAsync(
        TenantId tenantId,
        string subjectSid,
        WriteBudgetLimit limit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectSid);
        ArgumentNullException.ThrowIfNull(limit);
        ct.ThrowIfCancellationRequested();

        long effectiveStatementCap = limit.EffectiveStatementMaxRows();
        if (limit.StatementsPerMinute <= 0 || limit.RowsPerHour <= 0)
        {
            return new WriteBudgetAcquisition(false, 0, effectiveStatementCap, 60, "write_budget_exhausted");
        }

        long nowSec = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        string callerHash = InMemoryWriteBudgetStore.BuildCallerKey(tenantId, subjectSid);
        string stmtKey = $"{_keyPrefix}stmt:{callerHash}:{nowSec / 60}";
        string rowsKey = $"{_keyPrefix}rows:{callerHash}:{nowSec / 3600}";

        if (_redisDb is not null)
        {
            var res = (RedisResult[]?)await AcquireStatementLua.EvaluateAsync(
                _redisDb,
                new
                {
                    KEYS = new RedisKey[] { (RedisKey)stmtKey, (RedisKey)rowsKey },
                    ARGV = new RedisValue[]
                    {
                        (RedisValue)limit.StatementsPerMinute,
                        (RedisValue)limit.RowsPerHour,
                        (RedisValue)120
                    }
                }).ConfigureAwait(false);

            if (res is { Length: >= 2 })
            {
                bool allowed = (long)res[0] == 1;
                long remainingRows = (long)res[1];
                if (!allowed)
                {
                    int retryAfter = remainingRows <= 0
                        ? (int)Math.Max(1, 3600 - (nowSec % 3600))
                        : (int)Math.Max(1, 60 - (nowSec % 60));
                    return new WriteBudgetAcquisition(false, Math.Max(0, remainingRows), effectiveStatementCap, retryAfter, "write_budget_exhausted");
                }

                return new WriteBudgetAcquisition(true, remainingRows, effectiveStatementCap, 0);
            }

            return new WriteBudgetAcquisition(false, 0, effectiveStatementCap, 60, "write_budget_exhausted");
        }

        SemaphoreSlim gate = GetStripe(callerHash);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long usedRows = await ReadCountAsync(rowsKey, ct).ConfigureAwait(false);
            long remainingRows = limit.RowsPerHour - usedRows;
            if (remainingRows <= 0)
            {
                int retryAfterHour = (int)Math.Max(1, 3600 - (nowSec % 3600));
                return new WriteBudgetAcquisition(false, 0, effectiveStatementCap, retryAfterHour, "write_budget_exhausted");
            }

            long currentStmts = await ReadCountAsync(stmtKey, ct).ConfigureAwait(false);
            if (currentStmts >= limit.StatementsPerMinute)
            {
                int retryAfterMinute = (int)Math.Max(1, 60 - (nowSec % 60));
                return new WriteBudgetAcquisition(false, remainingRows, effectiveStatementCap, retryAfterMinute, "write_budget_exhausted");
            }

            await WriteCountAsync(stmtKey, currentStmts + 1, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            return new WriteBudgetAcquisition(true, remainingRows, effectiveStatementCap, 0);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> TryRecordAffectedRowsAsync(
        TenantId tenantId,
        string subjectSid,
        long affectedRows,
        long rowsPerHourLimit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectSid);
        ct.ThrowIfCancellationRequested();

        if (rowsPerHourLimit <= 0)
        {
            return false;
        }

        if (affectedRows <= 0)
        {
            return true;
        }

        long nowSec = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        string callerHash = InMemoryWriteBudgetStore.BuildCallerKey(tenantId, subjectSid);
        string rowsKey = $"{_keyPrefix}rows:{callerHash}:{nowSec / 3600}";

        if (_redisDb is not null)
        {
            var res = (long)await RecordRowsLua.EvaluateAsync(
                _redisDb,
                new
                {
                    KEYS = new RedisKey[] { (RedisKey)rowsKey },
                    ARGV = new RedisValue[]
                    {
                        (RedisValue)affectedRows,
                        (RedisValue)rowsPerHourLimit,
                        (RedisValue)7200
                    }
                }).ConfigureAwait(false);
            return res == 1;
        }

        SemaphoreSlim gate = GetStripe(callerHash);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long usedRows = await ReadCountAsync(rowsKey, ct).ConfigureAwait(false);
            if (usedRows > rowsPerHourLimit - affectedRows)
            {
                return false;
            }

            await WriteCountAsync(rowsKey, usedRows + affectedRows, TimeSpan.FromHours(2), ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<long> ReadCountAsync(string key, CancellationToken ct)
    {
        byte[]? bytes = await _distributedCache!.GetAsync(key, ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0)
        {
            return 0;
        }

        string text = Encoding.UTF8.GetString(bytes);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;
    }

    private Task WriteCountAsync(string key, long value, TimeSpan ttl, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture));
        return _distributedCache!.SetAsync(
            key,
            bytes,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
            ct);
    }

    private SemaphoreSlim GetStripe(string callerHash)
    {
        uint idx = (uint)callerHash.GetHashCode(StringComparison.Ordinal) % StripeCount;
        return _stripes![idx];
    }

    private static SemaphoreSlim[] CreateStripes()
    {
        var stripes = new SemaphoreSlim[StripeCount];
        for (int i = 0; i < StripeCount; i++)
        {
            stripes[i] = new SemaphoreSlim(1, 1);
        }

        return stripes;
    }
}
