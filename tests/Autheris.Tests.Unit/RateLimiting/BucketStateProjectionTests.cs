using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Autheris.Tests.Unit.RateLimiting;

public sealed class BucketStateProjectionTests
{
    private const string Key = "caller-projection";

    private static ClientQuotaPolicy Policy(double rate) =>
        new(ClientTier.Standard, 250, 10, 1000, rate, true);

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_GOV175B_F1_InMemory_ZeroRate_ProbeNeverProjectsRefill()
    {
        var clock = new FakeTimeProvider();
        var limiter = new InMemoryRateLimiterService(timeProvider: clock);
        var policy = Policy(0.0);

        (await limiter.CheckCostQuotaAsync(Key, 150, policy)).Allowed.ShouldBeTrue();
        clock.Advance(TimeSpan.FromMinutes(10));

        var probe = await limiter.GetBucketStateAsync(Key, policy);

        probe.ShouldNotBeNull();
        probe.CurrentBalance.ShouldBe(850.0);
        probe.RefillRatePerSecond.ShouldBe(0.0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_GOV175B_F1_InMemory_NegativeRate_IsTreatedAsZero()
    {
        var clock = new FakeTimeProvider();
        var limiter = new InMemoryRateLimiterService(timeProvider: clock);
        var policy = Policy(-5.0);

        (await limiter.CheckCostQuotaAsync(Key, 150, policy)).Allowed.ShouldBeTrue();
        clock.Advance(TimeSpan.FromMinutes(1));

        var probe = await limiter.GetBucketStateAsync(Key, policy);

        probe.ShouldNotBeNull();
        probe.CurrentBalance.ShouldBe(850.0);
        probe.RefillRatePerSecond.ShouldBe(0.0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_GOV175B_F1_InMemory_PositiveRate_ProjectsElapsedRefillCapped()
    {
        var clock = new FakeTimeProvider();
        var limiter = new InMemoryRateLimiterService(timeProvider: clock);
        var policy = Policy(2.0);

        (await limiter.CheckCostQuotaAsync(Key, 150, policy)).Allowed.ShouldBeTrue();
        clock.Advance(TimeSpan.FromSeconds(10));

        var probe = await limiter.GetBucketStateAsync(Key, policy);
        probe.ShouldNotBeNull();
        probe.CurrentBalance.ShouldBe(870.0);
        probe.RefillRatePerSecond.ShouldBe(2.0);

        clock.Advance(TimeSpan.FromHours(1));
        var capped = await limiter.GetBucketStateAsync(Key, policy);
        capped.ShouldNotBeNull();
        capped.CurrentBalance.ShouldBe(1000.0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_GOV175B_F1_Redis_ZeroRate_ProbeReportsStoredBalanceExactly()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000));
        var limiter = CreateRedis(clock, "750", clock.GetUtcNow().ToUnixTimeMilliseconds() - 600_000);

        var probe = await limiter.GetBucketStateAsync(Key, Policy(0.0));

        probe.ShouldNotBeNull();
        probe.CurrentBalance.ShouldBe(750.0);
        probe.RefillRatePerSecond.ShouldBe(0.0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_GOV175B_F1_Redis_PositiveRate_ProjectsElapsedRefill()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000));
        var limiter = CreateRedis(clock, "750", clock.GetUtcNow().ToUnixTimeMilliseconds() - 10_000);

        var probe = await limiter.GetBucketStateAsync(Key, Policy(2.0));

        probe.ShouldNotBeNull();
        probe.CurrentBalance.ShouldBe(770.0);
        probe.RefillRatePerSecond.ShouldBe(2.0);
    }

    private static RedisRateLimiterService CreateRedis(TimeProvider clock, string tokens, long lastRefillMs)
    {
        var db = Substitute.For<IDatabase>();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        db.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue[]>())
            .Returns(new RedisValue[] { (RedisValue)tokens, (RedisValue)lastRefillMs });

        return new RedisRateLimiterService(
            multiplexer,
            Options.Create(new GatewayOptions()),
            NullLogger<RedisRateLimiterService>.Instance,
            clock);
    }
}
