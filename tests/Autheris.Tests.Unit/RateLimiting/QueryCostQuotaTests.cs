namespace Autheris.Tests.Unit.Quota;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Common.Quota;
using Autheris.Application.State.Caching.Interfaces;
using Autheris.Application.State.Caching.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Interceptors;
using Autheris.Infrastructure.RateLimiting;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

public sealed class QueryCostQuotaTests : IDisposable
{
    public sealed class QuotaTestQuery
    {
        public static bool Executed { get; set; }

        public string GetGreeting()
        {
            Executed = true;
            return "hello";
        }
    }

    public QueryCostQuotaTests()
    {
        QuotaTestQuery.Executed = false;
        CatalogRelationRegistry.Clear();
    }

    public void Dispose()
    {
        QuotaTestQuery.Executed = false;
        CatalogRelationRegistry.Clear();
    }

    private static IRequestExecutor CreateExecutor(
        IRateLimiterService rateLimiter,
        IHttpContextAccessor httpContextAccessor,
        GatewayOptions gatewayOptions)
    {
        var services = new ServiceCollection();
        services.AddSingleton(rateLimiter);
        services.AddSingleton(httpContextAccessor);
        services.AddSingleton(Options.Create(gatewayOptions));
        services.AddSingleton<IClientTierResolver>(new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(gatewayOptions)));
        services.AddSingleton<IQueryCostQuota, QueryCostQuota>();

        return services
            .AddGraphQLServer()
            .AddQueryType<QuotaTestQuery>()
            .UseRequest<CostAndQuotaMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync()
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    public async Task GraphQl_BehaviourUnchanged_AfterExtraction()
    {
        // 1. OverCost test: query complexity exceeds client tier limit -> 400 BadRequest with QUERY_COST_QUOTA_EXCEEDED
        var gatewayOptions = new GatewayOptions
        {
            RateLimiting = new RateLimitingOptions
            {
                ClientTiers = new ClientTierOptions
                {
                    TierLimits = new Dictionary<string, ClientTierLimitOverride>
                    {
                        ["Standard"] = new ClientTierLimitOverride { MaxCostPerQuery = 0 } // artificially low limit
                    }
                }
            },
            QueryCost = new QueryCostOptions { BaseCost = 10, EnablePostExecutionSettlement = true }
        };

        var rateLimiter = Substitute.For<IRateLimiterService>();
        rateLimiter.ReserveCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CostQuotaResult(Allowed: true, RemainingTokens: 100, RetryAfterSeconds: 0)));
        rateLimiter.SettleCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CostQuotaResult(Allowed: true, RemainingTokens: 90, RetryAfterSeconds: 0)));

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "TestAuth"));
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        var executor = CreateExecutor(rateLimiter, accessor, gatewayOptions);

        var requestOverCost = OperationRequestBuilder.New()
            .SetDocument("{ greeting }")
            .Build();

        var resultOverCost = await executor.ExecuteAsync(requestOverCost);
        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        var opOverCost = resultOverCost.ShouldBeOfType<OperationResult>();
        opOverCost.Errors.ShouldNotBeNull();
        opOverCost.Errors[0].Code.ShouldBe("QUERY_COST_QUOTA_EXCEEDED");

        // 2. Exhausted quota test: rate limiter returns Allowed = false -> 429 TooManyRequests with RATE_LIMIT_EXCEEDED
        var gatewayOptionsExhausted = new GatewayOptions
        {
            RateLimiting = new RateLimitingOptions
            {
                ClientTiers = new ClientTierOptions
                {
                    TierLimits = new Dictionary<string, ClientTierLimitOverride>
                    {
                        ["Standard"] = new ClientTierLimitOverride { MaxCostPerQuery = 500 }
                    }
                }
            },
            QueryCost = new QueryCostOptions { BaseCost = 10, EnablePostExecutionSettlement = true }
        };

        rateLimiter.ReserveCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CostQuotaResult(Allowed: false, RemainingTokens: 0, RetryAfterSeconds: 45)));

        var httpContextExhausted = new DefaultHttpContext();
        httpContextExhausted.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "TestAuth"));
        var accessorExhausted = new HttpContextAccessor { HttpContext = httpContextExhausted };

        var executorExhausted = CreateExecutor(rateLimiter, accessorExhausted, gatewayOptionsExhausted);
        var resultExhausted = await executorExhausted.ExecuteAsync(requestOverCost);
        httpContextExhausted.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        httpContextExhausted.Response.Headers["Retry-After"].ToString().ShouldBe("45");
        var opExhausted = resultExhausted.ShouldBeOfType<OperationResult>();
        opExhausted.Errors.ShouldNotBeNull();
        opExhausted.Errors[0].Code.ShouldBe("RATE_LIMIT_EXCEEDED");

        // 3. Admitted & Settlement test: successful execution settles tokens and writes cost headers
        rateLimiter.ReserveCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CostQuotaResult(Allowed: true, RemainingTokens: 100, RetryAfterSeconds: 0)));
        rateLimiter.SettleCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CostQuotaResult(Allowed: true, RemainingTokens: 90, RetryAfterSeconds: 0)));

        var httpContextSuccess = new DefaultHttpContext();
        httpContextSuccess.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "TestAuth"));
        var accessorSuccess = new HttpContextAccessor { HttpContext = httpContextSuccess };

        var executorSuccess = CreateExecutor(rateLimiter, accessorSuccess, gatewayOptionsExhausted);
        var resultSuccess = await executorSuccess.ExecuteAsync(requestOverCost);
        httpContextSuccess.Response.Headers.ContainsKey("X-Query-Cost").ShouldBeTrue();
        httpContextSuccess.Response.Headers["X-RateLimit-Remaining"].ToString().ShouldBe("90");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task NeverAdmissible_IsOverCost_Not429()
    {
        // Estimate > MaxTokensCapacity -> OverCost (not QuotaExhausted), limiter Reserve never called
        var tierResolver = Substitute.For<IClientTierResolver>();
        var rateLimiter = Substitute.For<IRateLimiterService>();
        var gatewayOptions = Options.Create(new GatewayOptions());

        var policy = new ClientQuotaPolicy(
            Tier: ClientTier.Standard,
            MaxCostPerQuery: 500,
            MaxComplexityDepth: 10,
            MaxTokensCapacity: 100, // capacity = 100
            TokenRefillRatePerSecond: 10.0,
            ExposeCostExtensions: true);

        tierResolver.ResolveAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ClientQuotaContext("sub-1", ClientTier.Standard, policy)));

        var quota = new QueryCostQuota(tierResolver, rateLimiter, gatewayOptions);

        var request = new QueryCostRequest(
            Principal: new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "bob")], "Auth")),
            ApiKey: null,
            ClientIp: null,
            EstimatedCost: 150, // 150 > capacity (100)
            TransportMaxCost: 1000,
            CapByTierMaxCostPerQuery: true);

        var admission = await quota.AdmitAsync(request, CancellationToken.None);

        admission.Outcome.ShouldBe(QueryCostOutcome.OverCost);
        admission.MaxAllowedCost.ShouldBe(100);
        await rateLimiter.DidNotReceive().ReserveCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Settle_DebitsAboveReservation()
    {
        // Reserve 10, settle 50 -> balance debited 40 more, negative allowed (both limiter implementations)

        // 1. InMemoryRateLimiterService
        var inMemoryLimiter = new InMemoryRateLimiterService();
        var policy = new ClientQuotaPolicy(
            Tier: ClientTier.Standard,
            MaxCostPerQuery: 500,
            MaxComplexityDepth: 10,
            MaxTokensCapacity: 100,
            TokenRefillRatePerSecond: 0.0,
            ExposeCostExtensions: true);

        var key = "inmem-settle-" + Guid.NewGuid();

        // Initial capacity 100, reserve 10 -> remaining 90
        var res1 = await inMemoryLimiter.ReserveCostQuotaAsync(key, 10, policy, CancellationToken.None);
        res1.Allowed.ShouldBeTrue();
        res1.RemainingTokens.ShouldBe(90);

        // Settle reserved 10, actual 50 -> delta = 10 - 50 = -40 -> remaining 90 - 40 = 50
        var settle1 = await inMemoryLimiter.SettleCostQuotaAsync(key, 10, 50, policy, CancellationToken.None);
        settle1.Allowed.ShouldBeTrue();
        settle1.RemainingTokens.ShouldBe(50);

        // Reserve remaining 50 -> remaining 0
        var res2 = await inMemoryLimiter.ReserveCostQuotaAsync(key, 50, policy, CancellationToken.None);
        res2.Allowed.ShouldBeTrue();
        res2.RemainingTokens.ShouldBe(0);

        // Settle reserved 0, actual 40 -> delta = -40 -> balance -40 (negative allowed)
        var settle2 = await inMemoryLimiter.SettleCostQuotaAsync(key, 0, 40, policy, CancellationToken.None);
        settle2.Allowed.ShouldBeTrue();
        settle2.RemainingTokens.ShouldBe(-40);

        // 2. RedisRateLimiterService (mocked Redis database verifying negative balance clamping behavior)
        var db = Substitute.For<IDatabase>();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        // Simulate Lua script execution returning negative balance (-40)
        db.ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create(new RedisValue[] { (RedisValue)1, (RedisValue)(-40), (RedisValue)0 })));

        var redisLimiter = new RedisRateLimiterService(
            multiplexer,
            Options.Create(new GatewayOptions()),
            NullLogger<RedisRateLimiterService>.Instance);

        var redisSettle = await redisLimiter.SettleCostQuotaAsync("redis-key", 10, 50, policy, CancellationToken.None);
        redisSettle.Allowed.ShouldBeTrue();
        redisSettle.RemainingTokens.ShouldBe(-40);
    }

    [Fact]
    public async Task RateLimitingDisabled_Unmetered_CeilingApplies()
    {
        // Disabled -> Unmetered, over-ceiling still OverCost (quota requirement 51-10)
        var tierResolver = Substitute.For<IClientTierResolver>();
        var rateLimiter = Substitute.For<IRateLimiterService>();
        var gatewayOptions = Options.Create(new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { warn_disable_rate_limiting = true }
        });

        var policy = new ClientQuotaPolicy(
            Tier: ClientTier.Standard,
            MaxCostPerQuery: 500,
            MaxComplexityDepth: 10,
            MaxTokensCapacity: 1000,
            TokenRefillRatePerSecond: 10.0,
            ExposeCostExtensions: true);

        tierResolver.ResolveAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ClientQuotaContext("sub-1", ClientTier.Standard, policy)));

        var quota = new QueryCostQuota(tierResolver, rateLimiter, gatewayOptions);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "charlie")], "Auth"));

        // Case A: Within ceiling -> Unmetered
        var underCeilingRequest = new QueryCostRequest(
            Principal: principal,
            ApiKey: null,
            ClientIp: null,
            EstimatedCost: 50,
            TransportMaxCost: 100,
            CapByTierMaxCostPerQuery: false);

        var underCeilingAdmission = await quota.AdmitAsync(underCeilingRequest, CancellationToken.None);
        underCeilingAdmission.Outcome.ShouldBe(QueryCostOutcome.Unmetered);
        await rateLimiter.DidNotReceive().ReserveCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>());

        // Case B: Over ceiling -> OverCost
        var overCeilingRequest = new QueryCostRequest(
            Principal: principal,
            ApiKey: null,
            ClientIp: null,
            EstimatedCost: 150,
            TransportMaxCost: 100,
            CapByTierMaxCostPerQuery: false);

        var overCeilingAdmission = await quota.AdmitAsync(overCeilingRequest, CancellationToken.None);
        overCeilingAdmission.Outcome.ShouldBe(QueryCostOutcome.OverCost);
        overCeilingAdmission.MaxAllowedCost.ShouldBe(100);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReservationThrows_NeverUnmetered()
    {
        // Limiter throws -> AdmitAsync throws, never Unmetered (quota requirement 51-10)
        var tierResolver = Substitute.For<IClientTierResolver>();
        var rateLimiter = Substitute.For<IRateLimiterService>();
        var gatewayOptions = Options.Create(new GatewayOptions());

        var policy = new ClientQuotaPolicy(
            Tier: ClientTier.Standard,
            MaxCostPerQuery: 500,
            MaxComplexityDepth: 10,
            MaxTokensCapacity: 1000,
            TokenRefillRatePerSecond: 10.0,
            ExposeCostExtensions: true);

        tierResolver.ResolveAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ClientQuotaContext("sub-1", ClientTier.Standard, policy)));

        rateLimiter.ReserveCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns<Task<CostQuotaResult>>(_ => throw new InvalidOperationException("Redis connection pool exhausted"));

        var quota = new QueryCostQuota(tierResolver, rateLimiter, gatewayOptions);

        var request = new QueryCostRequest(
            Principal: new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "dave")], "Auth")),
            ApiKey: null,
            ClientIp: null,
            EstimatedCost: 50,
            TransportMaxCost: 500,
            CapByTierMaxCostPerQuery: true);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await quota.AdmitAsync(request, CancellationToken.None);
        });
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ChargeFailureFee_DebitsWithoutReservationCheck()
    {
        // Empty bucket -> debit of max(BaseCost, FailureFee) applied without reservation check (quota requirement 51-11)
        var tierResolver = Substitute.For<IClientTierResolver>();
        var inMemoryLimiter = new InMemoryRateLimiterService();
        var gatewayOptions = Options.Create(new GatewayOptions
        {
            QueryCost = new QueryCostOptions
            {
                BaseCost = 5,
                FailureFee = 15 // max(5, 15) = 15
            }
        });

        var policy = new ClientQuotaPolicy(
            Tier: ClientTier.Standard,
            MaxCostPerQuery: 500,
            MaxComplexityDepth: 10,
            MaxTokensCapacity: 100,
            TokenRefillRatePerSecond: 0.0,
            ExposeCostExtensions: true);

        var subjectId = "failure-fee-subject-" + Guid.NewGuid();
        tierResolver.ResolveAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ClientQuotaContext(subjectId, ClientTier.Standard, policy)));

        // Drain the bucket completely
        var drainResult = await inMemoryLimiter.ReserveCostQuotaAsync(subjectId, 100, policy, CancellationToken.None);
        drainResult.Allowed.ShouldBeTrue();
        drainResult.RemainingTokens.ShouldBe(0);

        // Regular reservation now fails
        var nextReservation = await inMemoryLimiter.ReserveCostQuotaAsync(subjectId, 1, policy, CancellationToken.None);
        nextReservation.Allowed.ShouldBeFalse();

        // ChargeFailureFeeAsync debits without reservation check
        var quota = new QueryCostQuota(tierResolver, inMemoryLimiter, gatewayOptions);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "eve")], "Auth"));

        await quota.ChargeFailureFeeAsync(principal, CancellationToken.None);

        // Bucket now has negative balance of -15 (debited failure fee without reservation gate)
        var probe = await inMemoryLimiter.GetBucketStateAsync(subjectId, policy, CancellationToken.None);
        probe.ShouldNotBeNull();
        probe.CurrentBalance.ShouldBe(-15.0);
    }
}
