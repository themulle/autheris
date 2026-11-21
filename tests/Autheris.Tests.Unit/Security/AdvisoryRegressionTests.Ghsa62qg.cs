namespace Autheris.Tests.Unit.Security;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed partial class AdvisoryRegressionTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task Ghsa_62qg_cw52_regression()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var db = Substitute.For<StackExchange.Redis.IDatabase>();
        var mux = Substitute.For<StackExchange.Redis.IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        db.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<StackExchange.Redis.RedisKey[]?>(),
                Arg.Any<StackExchange.Redis.RedisValue[]?>(),
                Arg.Any<StackExchange.Redis.CommandFlags>())
            .Returns(ci =>
            {
                var script = ci.ArgAt<string>(0);
                var keys = ci.ArgAt<StackExchange.Redis.RedisKey[]?>(1);
                var values = ci.ArgAt<StackExchange.Redis.RedisValue[]?>(2);
                keys.ShouldNotBeNull();
                values.ShouldNotBeNull();
                keys!.Length.ShouldBeGreaterThan(0);
                values!.Length.ShouldBeGreaterThan(0);

                if (script.Contains("requested_cost", StringComparison.Ordinal) || script.Contains("reserved_cost", StringComparison.Ordinal))
                {
                    return Task.FromResult(StackExchange.Redis.RedisResult.Create(new StackExchange.Redis.RedisValue[] { 1L, 90L, 0L }));
                }

                if (script.Contains("PostAuthSid", StringComparison.Ordinal) || script.Contains("wait_seconds", StringComparison.Ordinal))
                {
                    script.ShouldContain("math.max(last_refill, now_ms)");
                    return Task.FromResult(StackExchange.Redis.RedisResult.Create(new StackExchange.Redis.RedisValue[] { 1L, 0L }));
                }

                if (script.Contains("maxStmts", StringComparison.Ordinal))
                {
                    keys.Length.ShouldBe(2);
                    values.Length.ShouldBe(3);
                    return Task.FromResult(StackExchange.Redis.RedisResult.Create(new StackExchange.Redis.RedisValue[] { 1L, 100L }));
                }

                if (script.Contains("INCRBY", StringComparison.Ordinal))
                {
                    keys.Length.ShouldBe(1);
                    values.Length.ShouldBe(3);
                    return Task.FromResult(StackExchange.Redis.RedisResult.Create(1L));
                }

                return Task.FromResult(StackExchange.Redis.RedisResult.Create(new StackExchange.Redis.RedisValue[] { 1L, 60L }));
            });

        var limiter = new Autheris.Infrastructure.RateLimiting.RedisRateLimiterService(
            mux,
            Options.Create(new GatewayOptions()),
            NullLogger<Autheris.Infrastructure.RateLimiting.RedisRateLimiterService>.Instance,
            clock);

        (await limiter.CheckPreAuthIpAsync("203.0.113.10", new PreAuthIpRateLimitOptions { PermitLimit = 5, WindowSeconds = 60 })).Allowed.ShouldBeTrue();
        (await limiter.CheckPostAuthSidAsync("S-1-5-21-1", new PostAuthSidRateLimitOptions { TokenBucketCapacity = 10, TokensPerSecond = 2 })).Allowed.ShouldBeTrue();
        var policy = new ClientQuotaPolicy(ClientTier.Standard, 100, 10, 100, 10.0, true);
        (await limiter.CheckCostQuotaAsync("user:t:s", 10, policy)).Allowed.ShouldBeTrue();
        (await limiter.SettleCostQuotaAsync("user:t:s", 10, 5, policy)).Allowed.ShouldBeTrue();

        await db.Received(4).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Is<StackExchange.Redis.RedisKey[]?>(k => k != null && k.Length == 1),
            Arg.Is<StackExchange.Redis.RedisValue[]?>(v => v != null && v.Length >= 1),
            Arg.Any<StackExchange.Redis.CommandFlags>());

        var budgetStore = new Autheris.Infrastructure.RateLimiting.DistributedCacheWriteBudgetStore(mux, clock);
        var limit = new Autheris.Application.Policy.WriteGrants.WriteBudgetLimit(5, 100, 10, 50);
        (await budgetStore.TryAcquireStatementAsync(TenantA, "S-1-5-21-1", limit)).Allowed.ShouldBeTrue();
        (await budgetStore.TryRecordAffectedRowsAsync(TenantA, "S-1-5-21-1", 4, 100)).ShouldBeTrue();

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var distCache = Substitute.For<Microsoft.Extensions.Caching.Distributed.IDistributedCache>();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, distCache);
        var storageOptions = new GatewayOptions { Caching = new CachingOptions { Redis = new RedisOptions { Enabled = true, Configuration = "localhost:6379" } } };
        Autheris.Api.Extensions.DependencyInjection.GatewayStorageServiceExtensions.AddAutherisStorage(services, storageOptions);
        services.Last(d => d.ServiceType == typeof(Autheris.Application.Policy.WriteGrants.IWriteBudgetStore)).ShouldNotBeNull();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, mux);
        using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var resolvedBudget = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Autheris.Application.Policy.WriteGrants.IWriteBudgetStore>(sp);
        (await resolvedBudget.TryAcquireStatementAsync(TenantA, "S-1-5-21-2", limit)).Allowed.ShouldBeTrue();
        _ = distCache.DidNotReceiveWithAnyArgs().GetAsync(default!, default);
    }
}
