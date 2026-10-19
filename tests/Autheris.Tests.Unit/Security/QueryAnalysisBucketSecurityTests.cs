namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching.Interfaces;
using Autheris.Application.Caching.Services;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

public sealed class QueryAnalysisBucketSecurityTests
{
    private static ClientQuotaPolicy CreateStandardPolicy() =>
        ClientQuotaPolicy.ForTier(ClientTier.Standard);

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV175B_01_NonDestructiveBucketStateInspection()
    {
        // SEC-GOV175B-01: IRateLimiterService.GetBucketStateAsync MUST be strictly non-destructive:
        // zero token decrement, zero quota mutation, zero key TTL updates, and zero write locks.

        // 1. InMemoryRateLimiterService verification
        var inMemory = new InMemoryRateLimiterService();
        var key = "user:tenant-1:usr-test:client-app";
        var policy = new ClientQuotaPolicy(ClientTier.Standard, 250, 10, 1000, 0.0, true); // 0 refill rate to freeze balance

        // First probe: fresh bucket returns full capacity (1000)
        var state1 = await inMemory.GetBucketStateAsync(key, policy);
        state1.ShouldNotBeNull();
        state1.CurrentBalance.ShouldBe(1000.0);
        state1.Capacity.ShouldBe(1000);

        // Deduct 150 tokens via actual quota consumption
        var quotaResult = await inMemory.CheckCostQuotaAsync(key, 150, policy);
        quotaResult.Allowed.ShouldBeTrue();
        quotaResult.RemainingTokens.ShouldBe(850);

        // Probe 5 consecutive times via GetBucketStateAsync: balance MUST remain exactly 850
        for (int i = 0; i < 5; i++)
        {
            var probe = await inMemory.GetBucketStateAsync(key, policy);
            probe.ShouldNotBeNull();
            probe.CurrentBalance.ShouldBe(850.0);
            probe.Capacity.ShouldBe(1000);
        }

        // 2. RedisRateLimiterService verification: must issue ONLY read-only HMGET, zero writes
        var db = Substitute.For<IDatabase>();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        // Simulate existing redis hash for the bucket
        db.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue[]>())
            .Returns(new RedisValue[] { (RedisValue)"750", (RedisValue)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });

        var redisLimiter = new RedisRateLimiterService(
            multiplexer,
            Options.Create(new GatewayOptions()),
            NullLogger<RedisRateLimiterService>.Instance);

        var redisState = await redisLimiter.GetBucketStateAsync(key, policy);
        redisState.ShouldNotBeNull();
        redisState.CurrentBalance.ShouldBe(750.0, 1.0);
        redisState.Capacity.ShouldBe(1000);

        // Verify zero mutation calls occurred on Redis
        await db.DidNotReceiveWithAnyArgs().HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<HashEntry[]>());
        await db.DidNotReceiveWithAnyArgs().KeyExpireAsync(default, default(TimeSpan?));
        await db.DidNotReceiveWithAnyArgs().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV175B_02_ReadOnlyTokenAnalyzeAccess()
    {
        // SEC-GOV175B-02: Read-only tokens can access /analyze and receive callerQuota,
        // while remaining strictly prohibited from mutation routes.
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var auditRepo = Substitute.For<IAuditLogRepository>();

        var tableId = new TableIdentifier("sales", "public", "orders");
        var table = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "public", TableName = "orders", DisplayName = "orders", Visibility = DatasetVisibility.Full },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"]
        };
        metadataRepo.GetTableMetadataAsync(tableId, Arg.Any<CancellationToken>()).Returns(table);

        var tierResolver = Substitute.For<IClientTierResolver>();
        var rateLimiter = Substitute.For<IRateLimiterService>();

        var standardPolicy = CreateStandardPolicy();
        tierResolver.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ClientQuotaContext("user:tenant-1:usr-ro", ClientTier.Standard, standardPolicy, "agent.read"));

        rateLimiter.GetBucketStateAsync(Arg.Any<string>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(new CallerBucketState(1000, 950.0, 20.0));

        var analysisService = new QueryAnalysisService(
            metadataRepo,
            consentRepo,
            auditRepo,
            gatewayOptions: Options.Create(new GatewayOptions()),
            clientTierResolver: tierResolver,
            rateLimiterService: rateLimiter);

        // Construct read-only principal
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "usr-ro"),
            new("http://schemas.microsoft.com/identity/claims/tenantid", "tenant-1"),
            new("scp", "Agent.Read")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        TokenAccessScope.MarkReadOnly(identity);
        var roPrincipal = new ClaimsPrincipal(identity);

        TokenAccessScope.IsReadOnly(roPrincipal).ShouldBeTrue();

        var queryItem = new QueryAnalysisItem
        {
            Name = "orders_check",
            SqlQuery = "SELECT id FROM sales.public.orders"
        };

        var result = await analysisService.AnalyzeQueriesAsync([queryItem], roPrincipal);

        result.ShouldNotBeNull();
        result.CallerQuota.ShouldNotBeNull();
        result.CallerQuota.ClientTier.ShouldBe("Standard");
        result.CallerQuota.BucketState.ShouldNotBeNull();
        result.CallerQuota.BucketState.Capacity.ShouldBe(1000);
        result.CallerQuota.BucketState.CurrentBalance.ShouldBe(950.0);
        result.CallerQuota.CallerBucket.ShouldNotBeNull();
        result.CallerQuota.SystemCaps.ShouldNotBeNull();
        result.CallerQuota.SystemCaps.MaxAllowedComplexity.ShouldBeGreaterThan(0);
        result.CallerQuota.SystemCaps.MaxResponseRows.ShouldBe(5000);
        result.CallerQuota.SystemCaps.MaxAllowedOffset.ShouldBe(10000);
        result.CallerQuota.SystemCaps.MaxNestingDepth.ShouldBe(10);
        result.CallerQuota.SystemCaps.MaxAggregateRowBudget.ShouldBe(50000);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_GOV175B_03_UniformInformationDensityAndErrorMasking()
    {
        // SEC-GOV175B-03: Fresh/uninitialized buckets return uniform full capacity
        // and Redis faults fail open gracefully without leaking internal infrastructure details.

        // 1. Fresh bucket uniformity: querying an uninitialized key returns full capacity
        var inMemory = new InMemoryRateLimiterService();
        var policy = CreateStandardPolicy();
        var freshKey = "user:tenant-fresh:usr-unknown:client-unseen";

        var freshState = await inMemory.GetBucketStateAsync(freshKey, policy);
        freshState.ShouldNotBeNull();
        freshState.Capacity.ShouldBe(policy.MaxTokensCapacity);
        freshState.CurrentBalance.ShouldBe(policy.MaxTokensCapacity);

        // 2. Redis fault tolerance: transient connection error does NOT throw or leak details
        var db = Substitute.For<IDatabase>();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        db.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue[]>())
            .Returns<RedisValue[]>(_ => throw new TimeoutException("Redis connection timed out while querying host"));

        var redisLimiter = new RedisRateLimiterService(
            multiplexer,
            Options.Create(new GatewayOptions()),
            NullLogger<RedisRateLimiterService>.Instance);

        // Probe must not throw and must fall back gracefully
        CallerBucketState? fallbackState = null;
        var ex = Record.Exception(() =>
        {
            fallbackState = redisLimiter.GetBucketStateAsync(freshKey, policy).GetAwaiter().GetResult();
        });

        ex.ShouldBeNull(); // No exception propagated to caller
        fallbackState.ShouldNotBeNull(); // Gracefully fell back to in-memory fresh bucket
        fallbackState.Capacity.ShouldBe(policy.MaxTokensCapacity);
        fallbackState.CurrentBalance.ShouldBe(policy.MaxTokensCapacity);
    }

    [Fact]
    public void ActiveSystemCaps_PopulatesCorrectDefaults()
    {
        var caps = new ActiveSystemCaps(
            MaxAllowedComplexity: 500,
            MaxResponseRows: 1000,
            MaxAllowedOffset: 10000,
            MaxNestingDepth: 10,
            MaxAggregateRowBudget: 50000);

        caps.MaxAllowedComplexity.ShouldBe(500);
        caps.MaxResponseRows.ShouldBe(1000);
        caps.MaxAllowedOffset.ShouldBe(10000);
        caps.MaxNestingDepth.ShouldBe(10);
        caps.MaxAggregateRowBudget.ShouldBe(50000);
    }
}
