using Autheris.Tests.Common;

namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Messaging;
using Autheris.Infrastructure.RateLimiting;
using Autheris.Infrastructure.Security;
using Autheris.Infrastructure.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using StackExchange.Redis;
using Xunit;

/// <summary>
/// Real-Redis integration tests (Testcontainers) exercising every Redis Lua script in the repository
/// so script parameter binding and atomicity bugs cannot be hidden by mock-based unit tests.
/// </summary>
[Trait("Category", "Security")]
public sealed class RedisLuaScriptsIntegrationTests
{
    private static string UniquePrefix() => $"autheris:it:{Guid.NewGuid():N}:";

    private static IOptions<GatewayOptions> RedisOptions(string prefix) =>
        Options.Create(new GatewayOptions
        {
            Caching = new CachingOptions
            {
                Redis = new RedisOptions
                {
                    Enabled = true,
                    InstanceName = prefix
                }
            }
        });

    [RequiresContainerRuntimeFact]
    public async Task RateLimiter_LuaScripts_ExecuteAndEnforceClusterBudgetsOnRealRedis()
    {
        var connStr = await SharedDatabaseServers.RedisServerAsync();
        await using var mux = await ConnectionMultiplexer.ConnectAsync(connStr);
        var db = mux.GetDatabase();
        var prefix = UniquePrefix();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));

        var limiter = new RedisRateLimiterService(mux, RedisOptions(prefix), NullLogger<RedisRateLimiterService>.Instance, clock);

        // 1. Pre-auth IP fixed-window script
        var ipOpts = new PreAuthIpRateLimitOptions { PermitLimit = 2, WindowSeconds = 60 };
        (await limiter.CheckPreAuthIpAsync("198.51.100.7", ipOpts)).Allowed.ShouldBeTrue();
        (await limiter.CheckPreAuthIpAsync("198.51.100.7", ipOpts)).Allowed.ShouldBeTrue();
        var ipDenied = await limiter.CheckPreAuthIpAsync("198.51.100.7", ipOpts);
        ipDenied.Allowed.ShouldBeFalse();
        ipDenied.RetryAfterSeconds.ShouldBeGreaterThanOrEqualTo(1);

        // 2. Post-auth SID token-bucket script + monotonic last_refill under clock skew
        var sidOpts = new PostAuthSidRateLimitOptions { TokenBucketCapacity = 2, TokensPerSecond = 1 };
        (await limiter.CheckPostAuthSidAsync("S-1-5-21-99", sidOpts)).Allowed.ShouldBeTrue();

        var aheadClock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 10, TimeSpan.Zero));
        var aheadLimiter = new RedisRateLimiterService(mux, RedisOptions(prefix), NullLogger<RedisRateLimiterService>.Instance, aheadClock);
        (await aheadLimiter.CheckPostAuthSidAsync("S-1-5-21-skew", new PostAuthSidRateLimitOptions { TokenBucketCapacity = 10, TokensPerSecond = 1 })).Allowed.ShouldBeTrue();

        // Simulate lagging replica clock (5 seconds earlier); last_refill must remain at t+10s, not rewind to t+5s
        var laggingClock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 5, TimeSpan.Zero));
        var laggingLimiter = new RedisRateLimiterService(mux, RedisOptions(prefix), NullLogger<RedisRateLimiterService>.Instance, laggingClock);
        (await laggingLimiter.CheckPostAuthSidAsync("S-1-5-21-skew", new PostAuthSidRateLimitOptions { TokenBucketCapacity = 10, TokensPerSecond = 1 })).Allowed.ShouldBeTrue();

        var storedLastRefill = (long)await db.HashGetAsync($"{prefix}ratelimit:sid:S-1-5-21-skew", "last_refill");
        storedLastRefill.ShouldBe(new DateTimeOffset(2026, 11, 21, 12, 0, 10, TimeSpan.Zero).ToUnixTimeMilliseconds());

        // Exhaust SID bucket cleanly
        (await limiter.CheckPostAuthSidAsync("S-1-5-21-99", sidOpts)).Allowed.ShouldBeTrue();
        (await limiter.CheckPostAuthSidAsync("S-1-5-21-99", sidOpts)).Allowed.ShouldBeFalse();

        // 3. Cost token-bucket reserve and settle scripts
        var policy = new ClientQuotaPolicy(ClientTier.Standard, 100, 10, 50, 1.0, true);
        var res1 = await limiter.ReserveCostQuotaAsync("user:t1:s1", 30, policy);
        res1.Allowed.ShouldBeTrue();
        res1.RemainingTokens.ShouldBe(20);

        var resDenied = await limiter.ReserveCostQuotaAsync("user:t1:s1", 25, policy);
        resDenied.Allowed.ShouldBeFalse();
        resDenied.RetryAfterSeconds.ShouldBeGreaterThanOrEqualTo(1);

        var settled = await limiter.SettleCostQuotaAsync("user:t1:s1", 30, 10, policy);
        settled.Allowed.ShouldBeTrue();
        settled.RemainingTokens.ShouldBe(40);
    }

    [RequiresContainerRuntimeFact]
    public async Task WriteBudgetStore_LuaScripts_EnforceStatementAndRowCapsOnRealRedis()
    {
        var connStr = await SharedDatabaseServers.RedisServerAsync();
        await using var mux = await ConnectionMultiplexer.ConnectAsync(connStr);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var store = new DistributedCacheWriteBudgetStore(mux, clock, UniquePrefix());

        var tenant = new TenantId("acme");
        var limit = new WriteBudgetLimit(StatementsPerMinute: 2, RowsPerHour: 10, GrantMaxRowsPerStatement: 6, ConfiguredMaxAffectedRows: 100);

        (await store.TryAcquireStatementAsync(tenant, "S-1-5-21-W1", limit)).Allowed.ShouldBeTrue();
        (await store.TryRecordAffectedRowsAsync(tenant, "S-1-5-21-W1", 6, limit.RowsPerHour)).ShouldBeTrue();

        (await store.TryAcquireStatementAsync(tenant, "S-1-5-21-W1", limit)).Allowed.ShouldBeTrue();
        // Exceeding remaining hourly row budget (6 + 5 > 10) fails atomically
        (await store.TryRecordAffectedRowsAsync(tenant, "S-1-5-21-W1", 5, limit.RowsPerHour)).ShouldBeFalse();
        // Consuming remaining 4 rows succeeds (total 10)
        (await store.TryRecordAffectedRowsAsync(tenant, "S-1-5-21-W1", 4, limit.RowsPerHour)).ShouldBeTrue();

        // Statement cap or exhausted row cap blocks third statement
        (await store.TryAcquireStatementAsync(tenant, "S-1-5-21-W1", limit)).Allowed.ShouldBeFalse();
    }

    [RequiresContainerRuntimeFact]
    public async Task ClusterState_Revocation_Epoch_And_BasicAuth_LuaScripts_WorkOnRealRedis()
    {
        var connStr = await SharedDatabaseServers.RedisServerAsync();
        await using var mux = await ConnectionMultiplexer.ConnectAsync(connStr);
        var db = mux.GetDatabase();
        var prefix = UniquePrefix();
        var opts = RedisOptions(prefix);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));

        // 1. RedisClusterStateProvider: budget, distributed lease with monotonic fence, and bounded slots
        var cluster = new RedisClusterStateProvider(mux, opts, NullLogger<RedisClusterStateProvider>.Instance, clock);
        var (o1, c1) = await cluster.TryConsumeBudgetAsync("b1", 4, 10, TimeSpan.FromMinutes(1));
        o1.ShouldBe(BudgetConsumeOutcome.Consumed);
        c1.ShouldBe(4);
        var (o2, c2) = await cluster.TryConsumeBudgetAsync("b1", 7, 10, TimeSpan.FromMinutes(1));
        o2.ShouldBe(BudgetConsumeOutcome.Exhausted);
        c2.ShouldBe(4);

        var lease1 = await cluster.TryAcquireLeaseAsync("job-1", TimeSpan.FromSeconds(10));
        lease1.ShouldNotBeNull();
        (await cluster.TryAcquireLeaseAsync("job-1", TimeSpan.FromSeconds(10))).ShouldBeNull();
        var token1 = lease1!.Token;
        await lease1.DisposeAsync();

        var lease2 = await cluster.TryAcquireLeaseAsync("job-1", TimeSpan.FromSeconds(10));
        lease2.ShouldNotBeNull();
        lease2!.Token.ShouldBeGreaterThan(token1);
        await lease2.DisposeAsync();

        (await cluster.TryAcquireSlotAsync("slots", "m1", 1, TimeSpan.FromSeconds(30))).ShouldBe(true);
        (await cluster.TryAcquireSlotAsync("slots", "m2", 1, TimeSpan.FromSeconds(30))).ShouldBe(false);
        (await cluster.RenewSlotAsync("slots", "m1", TimeSpan.FromSeconds(30))).ShouldBeTrue();
        await cluster.ReleaseSlotAsync("slots", "m1");
        (await cluster.TryAcquireSlotAsync("slots", "m2", 1, TimeSpan.FromSeconds(30))).ShouldBe(true);

        // 2. RedisTokenRevocationService: longest TTL preservation script
        var revocation = new RedisTokenRevocationService(mux, opts, NullLogger<RedisTokenRevocationService>.Instance, clock);
        await revocation.RevokeAsync("jti-long", clock.GetUtcNow().AddMinutes(60));
        await revocation.RevokeAsync("jti-long", clock.GetUtcNow().AddMinutes(2));
        var revTtl = await db.KeyTimeToLiveAsync(revocation.BuildKey(TokenRevocationKeys.Normalize("jti-long")));
        revTtl.ShouldNotBeNull();
        revTtl!.Value.TotalMinutes.ShouldBeGreaterThan(30);

        // 3. EpochValidationService: rollback Lua recovery script
        var epochs = new EpochValidationService(opts, new InProcessChannelEventBus(), null, mux, timeProvider: clock);
        var table = new TableIdentifier("sales", "public", "orders");
        var e1 = await epochs.GetCurrentEpochAsync(table);
        await epochs.InvalidateEpochAsync(table);
        var e2 = await epochs.GetCurrentEpochAsync(table);
        e2.ShouldBeGreaterThan(e1);

        // Simulate Redis epoch key rollback / deletion
        await db.KeyDeleteAsync($"{prefix}epoch:{table.ToString().ToLowerInvariant()}");
        var restored = await epochs.GetCurrentEpochAsync(table);
        restored.ShouldBeGreaterThan(e2);

        // 4. BasicAuthAttemptGuard: atomic increment + lockout script
        var guard = new BasicAuthAttemptGuard(
            new BasicAuthOptions { Enabled = true, MaxFailedAttempts = 2, FailureWindowSeconds = 60 },
            clock,
            redis: mux);
        var attemptKey = prefix + "user|198.51.100.8";
        await guard.RecordFailureAsync(attemptKey);
        (await guard.IsLockedOutAsync(attemptKey)).ShouldBeFalse();
        await guard.RecordFailureAsync(attemptKey);
        (await guard.IsLockedOutAsync(attemptKey)).ShouldBeTrue();
    }
}
