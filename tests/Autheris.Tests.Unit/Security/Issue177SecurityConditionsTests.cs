namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching.Interfaces;
using Autheris.Application.Caching.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Collection("CatalogRelation")]
public sealed class Issue177SecurityConditionsTests : IDisposable
{
    public sealed class Address
    {
        public string City { get; set; } = "Berlin";
    }

    public sealed class Customer
    {
        public string Id { get; set; } = "cust-1";
        public Address Address { get; set; } = new();
    }

    public sealed class Order
    {
        public string Id { get; set; } = "order-1";
        public Customer Customer { get; set; } = new();
        public Customer UncatalogedCustomer { get; set; } = new();
    }

    public sealed class SecurityTestQuery
    {
        public static bool ResolverExecuted { get; set; }
        public static bool ThrowException { get; set; }

        public List<Order> GetOrders(int first = 10)
        {
            ResolverExecuted = true;
            if (ThrowException)
            {
                throw new InvalidOperationException("Simulated database failure during query execution.");
            }
            return [new()];
        }
    }

    public Issue177SecurityConditionsTests()
    {
        SecurityTestQuery.ResolverExecuted = false;
        SecurityTestQuery.ThrowException = false;
        CatalogRelationRegistry.Clear();
    }

    public void Dispose()
    {
        SecurityTestQuery.ResolverExecuted = false;
        SecurityTestQuery.ThrowException = false;
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

        var sp = services.BuildServiceProvider();

        return new ServiceCollection()
            .AddSingleton(rateLimiter)
            .AddSingleton(httpContextAccessor)
            .AddSingleton(Options.Create(gatewayOptions))
            .AddSingleton<IClientTierResolver>(sp.GetRequiredService<IClientTierResolver>())
            .AddGraphQLServer()
            .AddQueryType<SecurityTestQuery>()
            .UseRequest<CostAndQuotaMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync()
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV177_01_PreAdmission_InsufficientTokens_RejectsWith429BeforeExecution()
    {
        // SEC-GOV177-01: When bucket balance is insufficient for reservedCost, reject with 429 immediately and execute 0 resolvers.
        var rateLimiter = Substitute.For<IRateLimiterService>();
        rateLimiter.ReserveCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CostQuotaResult(Allowed: false, RemainingTokens: 2, RetryAfterSeconds: 30)));

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "TestAuth"));
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        var gatewayOptions = new GatewayOptions();
        var executor = CreateExecutor(rateLimiter, accessor, gatewayOptions);

        var query = "{ orders(first: 10) { id } }";
        var result = await executor.ExecuteAsync(OperationRequestBuilder.New().SetDocument(query).Build());

        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        httpContext.Response.Headers["Retry-After"].ToString().ShouldBe("30");
        SecurityTestQuery.ResolverExecuted.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV177_02_Refund_ClampedAtMaxTokensCapacity_NoCreditMinting()
    {
        // SEC-GOV177-02: Refunds must never increase balance beyond MaxTokensCapacity.
        var limiter = new Autheris.Infrastructure.RateLimiting.InMemoryRateLimiterService();
        var policy = new ClientQuotaPolicy(
            Tier: ClientTier.Standard,
            MaxCostPerQuery: 500,
            MaxComplexityDepth: 10,
            MaxTokensCapacity: 1000,
            TokenRefillRatePerSecond: 0.0,
            ExposeCostExtensions: true);

        var key = "sec-client-" + Guid.NewGuid();

        // 1. Initial reservation of 100 on 1000-capacity bucket -> 900
        var res1 = await limiter.ReserveCostQuotaAsync(key, 100, policy, CancellationToken.None);
        res1.Allowed.ShouldBeTrue();
        res1.RemainingTokens.ShouldBe(900);

        // 2. Malicious settlement claiming refund of 500 tokens (reserved 600, actual 100 -> delta +500)
        var settle = await limiter.SettleCostQuotaAsync(key, 600, 100, policy, CancellationToken.None);

        settle.Allowed.ShouldBeTrue();
        // 900 + 500 = 1400, but clamped strictly at 1000 (MaxTokensCapacity)
        settle.RemainingTokens.ShouldBe(1000);
        settle.RemainingTokens.ShouldBeLessThanOrEqualTo(policy.MaxTokensCapacity);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV177_03_DebitAdjustment_ClampedAtZero_NoNegativeUnderflow()
    {
        // SEC-GOV177-02: Debit adjustments must clamp at 0 and never underflow to negative numbers.
        var limiter = new Autheris.Infrastructure.RateLimiting.InMemoryRateLimiterService();
        var policy = new ClientQuotaPolicy(
            Tier: ClientTier.Free,
            MaxCostPerQuery: 100,
            MaxComplexityDepth: 5,
            MaxTokensCapacity: 100,
            TokenRefillRatePerSecond: 0.0,
            ExposeCostExtensions: false);

        var key = "sec-client-" + Guid.NewGuid();

        // 1. Reserve 95 tokens -> 5 tokens remaining
        await limiter.ReserveCostQuotaAsync(key, 95, policy, CancellationToken.None);

        // 2. Actual work heavily exceeds reserved (reserved 95, actual 300 -> delta = -205)
        var settle = await limiter.SettleCostQuotaAsync(key, 95, 300, policy, CancellationToken.None);

        settle.Allowed.ShouldBeTrue();
        // Clamped at 0, never negative
        settle.RemainingTokens.ShouldBe(0);
        settle.RemainingTokens.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV177_04_QueryAbortOrException_IncursMinimumBaseFee()
    {
        // SEC-GOV177-03: Failed or aborted queries must incur at least BaseCost / FailureFee (no zero-cost queries).
        var limiter = new Autheris.Infrastructure.RateLimiting.InMemoryRateLimiterService();
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "TestAuth"));
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        var gatewayOptions = new GatewayOptions();
        gatewayOptions.QueryCost.BaseCost = 2;
        gatewayOptions.QueryCost.FailureFee = 5;
        gatewayOptions.RateLimiting.ClientTiers.TierLimits["Standard"] = new()
        {
            TokenRefillRatePerSecond = 0.0,
            MaxTokensCapacity = 1000
        };

        var executor = CreateExecutor(limiter, accessor, gatewayOptions);

        SecurityTestQuery.ThrowException = true;

        var query = "{ orders(first: 10) { id } }";
        var result = await executor.ExecuteAsync(OperationRequestBuilder.New().SetDocument(query).Build());
        var opRes = result.ExpectOperationResult();
        (opRes.Errors?.Count ?? 0).ShouldBeGreaterThan(0);

        // Alice's bucket should have incurred FailureFee (5 tokens)
        var tierResolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(gatewayOptions));
        var clientCtx = await tierResolver.ResolveAsync(httpContext.User, null, null);
        var balanceCheck = await limiter.ReserveCostQuotaAsync(clientCtx.SubjectId, 0, clientCtx.Policy, CancellationToken.None);
        // Standard tier capacity is 1000 -> 1000 - 5 = 995
        balanceCheck.RemainingTokens.ShouldBe(995);
        httpContext.Response.Headers["X-Query-Cost-Actual"].ToString().ShouldBe("5");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV177_05_ConcurrentSettlement_AtomicRedisLua_NoLostUpdates()
    {
        // SEC-GOV177-04: Concurrency test with 50 parallel requests verifying atomicity of settlement.
        var limiter = new Autheris.Infrastructure.RateLimiting.InMemoryRateLimiterService();
        var policy = new ClientQuotaPolicy(
            Tier: ClientTier.Enterprise,
            MaxCostPerQuery: 5000,
            MaxComplexityDepth: 20,
            MaxTokensCapacity: 20000,
            TokenRefillRatePerSecond: 0.0,
            ExposeCostExtensions: true);

        var key = "sec-concurrent-" + Guid.NewGuid();

        int parallelRequests = 50;
        int reservedCost = 40;
        int actualCost = 25; // net debit = 25 per request -> total debit = 50 * 25 = 1250

        var tasks = new List<Task>();
        for (int i = 0; i < parallelRequests; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var r = await limiter.ReserveCostQuotaAsync(key, reservedCost, policy, CancellationToken.None);
                r.Allowed.ShouldBeTrue();
                await limiter.SettleCostQuotaAsync(key, reservedCost, actualCost, policy, CancellationToken.None);
            }));
        }

        await Task.WhenAll(tasks);

        var finalCheck = await limiter.ReserveCostQuotaAsync(key, 0, policy, CancellationToken.None);
        // Exactly 20000 - 1250 = 18750
        finalCheck.RemainingTokens.ShouldBe(18750);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV177_06_UncatalogedOrAmbiguousField_FailsClosedToDefaultListMultiplier()
    {
        // SEC-GOV177-05: Uncataloged or ambiguous object fields fail closed to DefaultListMultiplier (10) rather than factor 1.
        var schema = (await new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<SecurityTestQuery>()
            .BuildRequestExecutorAsync()).Schema;

        // Register ONLY customer as ManyToOne
        CatalogRelationRegistry.Register("Order", "customer", RelationCardinality.ManyToOne);
        CatalogRelationRegistry.Register("Customer", "address", RelationCardinality.OneToOne);
        // Order.uncatalogedCustomer is NOT registered

        var docVerified = Utf8GraphQLParser.Parse("{ orders(first: 10) { customer { id } } }");
        var docUncataloged = Utf8GraphQLParser.Parse("{ orders(first: 10) { uncatalogedCustomer { id } } }");

        int costVerified = QueryCostAnalyzerRule.CalculateCost(docVerified, schema);
        int costUncataloged = QueryCostAnalyzerRule.CalculateCost(docUncataloged, schema);

        // Uncataloged field must fail closed and incur higher cost
        costUncataloged.ShouldBeGreaterThan(costVerified);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV177_07_UnauthenticatedRequest_BypassesTokenBucketAllocation()
    {
        // SEC-GOV177-06: Unauthenticated requests return 401 without creating or debiting token bucket keys.
        var rateLimiter = Substitute.For<IRateLimiterService>();
        var httpContext = new DefaultHttpContext();
        // No user, no API key
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        var gatewayOptions = new GatewayOptions(); // IsAnonymousAccessAllowed == false
        var executor = CreateExecutor(rateLimiter, accessor, gatewayOptions);

        var query = "{ orders(first: 10) { id } }";
        var result = await executor.ExecuteAsync(OperationRequestBuilder.New().SetDocument(query).Build());

        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        // Rate limiter must NOT be called at all
        await rateLimiter.DidNotReceiveWithAnyArgs().ReserveCostQuotaAsync(default!, default, default!, default);
        await rateLimiter.DidNotReceiveWithAnyArgs().CheckCostQuotaAsync(default!, default, default!, default);
        await rateLimiter.DidNotReceiveWithAnyArgs().SettleCostQuotaAsync(default!, default, default, default!, default);
    }
}
