namespace Autheris.Tests.Unit.RateLimiting;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.RateLimiting;
using Shouldly;
using Xunit;

public sealed class RateLimiterSettlementTests
{
    private readonly InMemoryRateLimiterService _limiter = new();

    private static ClientQuotaPolicy CreatePolicy(
        int maxCapacity = 1000,
        double refillRate = 0.0,
        int maxCost = 500)
    {
        return new ClientQuotaPolicy(
            Tier: ClientTier.Standard,
            MaxCostPerQuery: maxCost,
            MaxComplexityDepth: 10,
            MaxTokensCapacity: maxCapacity,
            TokenRefillRatePerSecond: refillRate,
            ExposeCostExtensions: true);
    }

    [Fact]
    public async Task ReserveCostQuotaAsync_DeductsTokensUpfront()
    {
        var policy = CreatePolicy(maxCapacity: 500);
        var key = "test-client-" + Guid.NewGuid();

        var decision = await _limiter.ReserveCostQuotaAsync(key, 100, policy, CancellationToken.None);

        decision.Allowed.ShouldBeTrue();
        decision.RemainingTokens.ShouldBe(400);
    }

    [Fact]
    public async Task SettleCostQuotaAsync_RefundsUnusedTokens()
    {
        var policy = CreatePolicy(maxCapacity: 1000);
        var key = "test-client-" + Guid.NewGuid();

        // 1. Reserve 100 tokens upfront (balance: 1000 - 100 = 900)
        var res1 = await _limiter.ReserveCostQuotaAsync(key, 100, policy, CancellationToken.None);
        res1.Allowed.ShouldBeTrue();
        res1.RemainingTokens.ShouldBe(900);

        // 2. Settle with actual work = 20 tokens -> refund delta = 80 tokens (balance: 900 + 80 = 980)
        var settle = await _limiter.SettleCostQuotaAsync(key, 100, 20, policy, CancellationToken.None);

        settle.Allowed.ShouldBeTrue();
        settle.RemainingTokens.ShouldBe(980);
    }

    [Fact]
    public async Task SettleCostQuotaAsync_ClampsAtMaxCapacity_NeverMintsSurplusTokens()
    {
        var policy = CreatePolicy(maxCapacity: 1000);
        var key = "test-client-" + Guid.NewGuid();

        // 1. Reserve 50 tokens (balance: 950)
        await _limiter.ReserveCostQuotaAsync(key, 50, policy, CancellationToken.None);

        // 2. Attempt huge refund: reserved 50, actual 0, but try settling reserved 200, actual 0 (delta +200)
        var settle = await _limiter.SettleCostQuotaAsync(key, 200, 0, policy, CancellationToken.None);

        settle.Allowed.ShouldBeTrue();
        // Tokens must clamp to MaxTokensCapacity (1000), never 1150
        settle.RemainingTokens.ShouldBe(1000);
    }

    [Fact]
    public async Task SettleCostQuotaAsync_AppliesAdditionalDebit_WhenActualExceedsReserved()
    {
        var policy = CreatePolicy(maxCapacity: 500);
        var key = "test-client-" + Guid.NewGuid();

        // 1. Reserve 50 tokens (balance: 450)
        await _limiter.ReserveCostQuotaAsync(key, 50, policy, CancellationToken.None);

        // 2. Settle with actual cost 80 (delta = 50 - 80 = -30) -> balance: 450 - 30 = 420
        var settle = await _limiter.SettleCostQuotaAsync(key, 50, 80, policy, CancellationToken.None);

        settle.Allowed.ShouldBeTrue();
        settle.RemainingTokens.ShouldBe(420);
    }

    [Fact]
    public async Task SettleCostQuotaAsync_ClampsAtZero_NeverUnderflowsOrBecomesNegative()
    {
        var policy = CreatePolicy(maxCapacity: 100);
        var key = "test-client-" + Guid.NewGuid();

        // 1. Reserve 90 tokens (balance: 10)
        await _limiter.ReserveCostQuotaAsync(key, 90, policy, CancellationToken.None);

        // 2. Actual work is huge (500 tokens, delta = 90 - 500 = -410)
        var settle = await _limiter.SettleCostQuotaAsync(key, 90, 500, policy, CancellationToken.None);

        settle.Allowed.ShouldBeTrue();
        settle.RemainingTokens.ShouldBe(0);
    }

    [Fact]
    public async Task ConcurrentSettlement_IsThreadSafeAndConsistent()
    {
        var policy = CreatePolicy(maxCapacity: 10000);
        var key = "test-concurrent-" + Guid.NewGuid();

        // Run 50 parallel reservations and settlements
        int concurrency = 50;
        int reserved = 20;
        int actual = 10; // each should net -10 tokens
        var tasks = new List<Task>();

        for (int i = 0; i < concurrency; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var r = await _limiter.ReserveCostQuotaAsync(key, reserved, policy, CancellationToken.None);
                r.Allowed.ShouldBeTrue();
                await _limiter.SettleCostQuotaAsync(key, reserved, actual, policy, CancellationToken.None);
            }));
        }

        await Task.WhenAll(tasks);

        // Initial 10000 - (50 * 10) = 9500
        var check = await _limiter.ReserveCostQuotaAsync(key, 0, policy, CancellationToken.None);
        check.RemainingTokens.ShouldBe(9500);
    }
}
