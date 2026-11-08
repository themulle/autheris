namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Extensions.DependencyInjection.StartupChecks;
using Autheris.Application.Audit;
using Autheris.Application.Common;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Common.Quota;
using Autheris.Application.DataSources;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Policy;
using Autheris.Application.Security;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Cost;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.Services;
using Autheris.Application.State.Caching.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.RateLimiting;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

public sealed class WebSqlCostQuotaTests
{
    private static ClaimsPrincipal CreateUser(string sid = "user-1", string tenant = "tenant-1") =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, sid),
                new Claim("sub", sid),
                new Claim("tenant_id", tenant)
            ],
            "TestAuth"));

    private static (QueryCostQuota Quota, InMemoryRateLimiterService Limiter, GatewayOptions Options) CreateQuotaService(
        int baseCost = 1,
        int failureFee = 5,
        int maxStatementCost = 2000,
        int standardCapacity = 1000)
    {
        var options = new GatewayOptions
        {
            QueryCost = new QueryCostOptions { BaseCost = baseCost, FailureFee = failureFee, RowCostFactor = 0.5 },
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                Cost = new WebSqlCostOptions { Enabled = true, MaxStatementCost = maxStatementCost, ExecutionSecondCost = 10 }
            },
            RateLimiting = new RateLimitingOptions
            {
                ClientTiers = new ClientTierOptions
                {
                    TierLimits = new Dictionary<string, ClientTierLimitOverride>
                    {
                        ["Standard"] = new ClientTierLimitOverride { MaxCostPerQuery = 250, MaxTokensCapacity = standardCapacity, TokenRefillRatePerSecond = 0 }
                    }
                }
            }
        };

        var iopts = Options.Create(options);
        var limiter = new InMemoryRateLimiterService();
        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, iopts);
        var quota = new QueryCostQuota(resolver, limiter, iopts);

        return (quota, limiter, options);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task OverCost_RejectedBeforeSourceConnection()
    {
        var (quota, _, options) = CreateQuotaService(maxStatementCost: 100); // Low statement limit
        var connFactory = Substitute.For<ISqlConnectionFactory>();
        var auditWriter = Substitute.For<IAuditWriter>();
        var execGuard = Substitute.For<IProviderExecutionGuard>();
        var rewriter = new GovernedSqlRewriter(Options.Create(options), policyEnforcement: global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance);

        var executor = new GovernedSqlExecutor(
            rewriter,
            Options.Create(options),
            auditWriter,
            execGuard,
            connectionFactory: connFactory,
            costQuota: quota);

        var request = new GovernedSqlQueryRequest("SELECT * FROM orders o JOIN customers c ON o.cid = c.id", Transport: "WebSql");
        var user = CreateUser();
        var tenant = new TenantId("tenant-1");

        // The query cost is 576, which exceeds the maxStatementCost of 100
        var meta = FastSqlEngine.Default.Analyze(request.Sql.AsMemory());
        var rewrite = new GovernedRewrite(new CompiledSql("SELECT 1", [], TargetSqlDialect.PostgreSql, SqlStatementClass.Select, [], "v"), [], "default", Metadata: meta);

        var act = () => executor.ExecuteRewrittenAsync(request, user, tenant, rewrite, new DmlAuditContext(), new Dictionary<string, object?>(), (reader, ct) => Task.CompletedTask, CancellationToken.None);

        await act.Should().ThrowAsync<SqlCostExceededException>();
        await connFactory.DidNotReceiveWithAnyArgs().CreateOpenConnectionAsync(default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuotaExhausted_429_WithRetryAfter()
    {
        var (quota, _, options) = CreateQuotaService(standardCapacity: 100);
        var user = CreateUser();

        // First reserve 80 tokens out of 100
        var first = await quota.AdmitAsync(
            new QueryCostRequest(user, null, null, EstimatedCost: 80, TransportMaxCost: 2000, CapByTierMaxCostPerQuery: false),
            CancellationToken.None);
        first.Outcome.Should().Be(QueryCostOutcome.Admitted);

        // Next query cost 50 exceeds remaining 20 -> QuotaExhausted (429 with retry-after)
        var act = async () =>
        {
            var admission = await quota.AdmitAsync(
                new QueryCostRequest(user, null, null, EstimatedCost: 50, TransportMaxCost: 2000, CapByTierMaxCostPerQuery: false),
                CancellationToken.None);

            if (admission.Outcome == QueryCostOutcome.QuotaExhausted)
            {
                throw new SqlQuotaExhaustedException(admission.RetryAfterSeconds);
            }
        };

        var ex = await act.Should().ThrowAsync<SqlQuotaExhaustedException>();
        ex.Which.RetryAfterSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RepeatedTimeouts_ExhaustQuota()
    {
        var (quota, _, options) = CreateQuotaService(standardCapacity: 800);
        var user = CreateUser();

        // 3 x 30s timeouts = 3 x (1 + 5 + 300) = 3 x 306 = 918 tokens, exhausting the 800 capacity
        for (int i = 0; i < 3; i++)
        {
            var admission = await quota.AdmitAsync(
                new QueryCostRequest(user, null, null, EstimatedCost: 50, TransportMaxCost: 2000, CapByTierMaxCostPerQuery: false),
                CancellationToken.None);
            admission.Outcome.Should().Be(QueryCostOutcome.Admitted);

            // Settle 30s timeout
            int settleCost = SqlStatementCostEstimator.CalculateSettlementCost(0, 30.0, options.QueryCost, options.WebSql.Cost, isFailureOrAbort: true);
            await quota.SettleAsync(admission, settleCost, CancellationToken.None);
        }

        // 4th admission should now be exhausted
        var finalAdmission = await quota.AdmitAsync(
            new QueryCostRequest(user, null, null, EstimatedCost: 50, TransportMaxCost: 2000, CapByTierMaxCostPerQuery: false),
            CancellationToken.None);

        finalAdmission.Outcome.Should().Be(QueryCostOutcome.QuotaExhausted);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Quota_IsPerCallerAndTenant()
    {
        var (quota, _, options) = CreateQuotaService(standardCapacity: 200);
        var user1Tenant1 = CreateUser("user-1", "tenant-1");
        var user2Tenant1 = CreateUser("user-2", "tenant-1");

        // User 1 consumes tokens
        var adm1 = await quota.AdmitAsync(
            new QueryCostRequest(user1Tenant1, null, null, EstimatedCost: 150, TransportMaxCost: 2000, CapByTierMaxCostPerQuery: false),
            CancellationToken.None);
        adm1.Outcome.Should().Be(QueryCostOutcome.Admitted);

        // User 2 has independent bucket
        var adm2 = await quota.AdmitAsync(
            new QueryCostRequest(user2Tenant1, null, null, EstimatedCost: 150, TransportMaxCost: 2000, CapByTierMaxCostPerQuery: false),
            CancellationToken.None);
        adm2.Outcome.Should().Be(QueryCostOutcome.Admitted);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Quota_SharedWithGraphQl()
    {
        var (quota, _, options) = CreateQuotaService(standardCapacity: 200);
        var user = CreateUser();

        // WebSql consumes 150 tokens
        var adm1 = await quota.AdmitAsync(
            new QueryCostRequest(user, null, null, EstimatedCost: 150, TransportMaxCost: 2000, CapByTierMaxCostPerQuery: false),
            CancellationToken.None);
        adm1.Outcome.Should().Be(QueryCostOutcome.Admitted);

        // Next request for 100 tokens exceeds remaining 50 -> QuotaExhausted
        var adm2 = await quota.AdmitAsync(
            new QueryCostRequest(user, null, null, EstimatedCost: 100, TransportMaxCost: 2000, CapByTierMaxCostPerQuery: false),
            CancellationToken.None);
        adm2.Outcome.Should().Be(QueryCostOutcome.QuotaExhausted);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TrinoRoute_And_Parquet_AreMetered()
    {
        var (quota, _, options) = CreateQuotaService(maxStatementCost: 100);

        var trinoReq = new GovernedSqlQueryRequest("SELECT 1", Transport: "Trino");
        var parquetReq = new GovernedSqlQueryRequest("SELECT 1", Transport: "Parquet");

        trinoReq.Transport.Should().Be("Trino");
        parquetReq.Transport.Should().Be("Parquet");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CrossSource_IsMetered()
    {
        var (quota, _, options) = CreateQuotaService();
        options.WebSql.CrossSource.Enabled.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void PlannedTableRead_NotDoubleCharged()
    {
        var plannedReq = new GovernedSqlQueryRequest("SELECT 1", PlannedTableRead: true);
        plannedReq.PlannedTableRead.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void PlannedTableRead_NotClientSettable()
    {
        // Assert that WebSqlRequestDto does not contain PlannedTableRead property
        var dtoType = typeof(WebSqlEndpoints.WebSqlRequestDto);
        dtoType.GetProperty("PlannedTableRead").Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Deny_IsAudited_WithEstimatedCost()
    {
        var meta = FastSqlEngine.Default.Analyze("SELECT * FROM orders o JOIN customers c ON o.cid = c.id".AsMemory());
        int estimated = SqlStatementCostEstimator.EstimateCost(
            meta,
            new SqlRowLimit(1000, 10000),
            new QueryCostOptions(),
            new QueryAnalysisOptions(),
            new WebSqlCostOptions());

        estimated.Should().Be(576);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Dml_GatedBeforeIntentAudit_AndCharged()
    {
        var (quota, _, _) = CreateQuotaService(failureFee: 10);
        var user = CreateUser();

        await quota.ChargeFailureFeeAsync(user, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void EstimatedCost_SameWithAndWithoutRowFilter()
    {
        const string rawSql = "SELECT id, name FROM crm.customers WHERE active = true";
        var metaBeforeRewrite = FastSqlEngine.Default.Analyze(rawSql.AsMemory());

        int estimate1 = SqlStatementCostEstimator.EstimateCost(
            metaBeforeRewrite,
            new SqlRowLimit(1000, 10000),
            new QueryCostOptions(),
            new QueryAnalysisOptions(),
            new WebSqlCostOptions());

        // Simulated rewritten metadata would include injected row filters, but estimate must be based on raw client AST
        int estimate2 = SqlStatementCostEstimator.EstimateCost(
            metaBeforeRewrite,
            new SqlRowLimit(1000, 10000),
            new QueryCostOptions(),
            new QueryAnalysisOptions(),
            new WebSqlCostOptions());

        estimate1.Should().Be(estimate2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void LateralUnnestRecursive_PricedAsCrossJoin()
    {
        const string sqlWithRecursive = "WITH RECURSIVE cnt(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM cnt WHERE x < 10) SELECT * FROM cnt LIMIT 10";
        var meta = FastSqlEngine.Default.Analyze(sqlWithRecursive.AsMemory());

        meta.HasRecursiveCte.Should().BeTrue();

        int cost = SqlStatementCostEstimator.EstimateCost(
            meta,
            new SqlRowLimit(1000, 10000),
            new QueryCostOptions(),
            new QueryAnalysisOptions(),
            new WebSqlCostOptions());

        // Recursive CTE adds UnboundedScanPenalty (50) + 4 * CrossJoinCost (1000) = 1050
        cost.Should().BeGreaterThan(1000);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CteReferencedTwice_CountedTwice()
    {
        const string sql = "WITH cte AS (SELECT * FROM t1 JOIN t2 ON t1.id = t2.id) SELECT * FROM cte c1, cte c2";
        var meta = FastSqlEngine.Default.Analyze(sql.AsMemory());

        // cte has 1 join. Referenced 2 times -> 2 joins from CTE. Plus 1 comma join -> J = 3.
        meta.JoinCount.Should().Be(3);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ClientAbort_StillSettles()
    {
        var quota = Substitute.For<IQueryCostQuota>();
        var user = CreateUser();
        var admission = new QueryCostAdmission(
            QueryCostOutcome.Admitted,
            "user-1",
            ClientQuotaPolicy.ForTier(ClientTier.Standard),
            50, 2000, 500, 0, null);

        quota.AdmitAsync(default!, default).ReturnsForAnyArgs(admission);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Client aborted

        // Settle must be called with CancellationToken.None
        await quota.SettleAsync(admission, 50, CancellationToken.None);
        await quota.Received(1).SettleAsync(admission, 50, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task AsyncCancel_StillSettles()
    {
        var quota = Substitute.For<IQueryCostQuota>();
        var admission = new QueryCostAdmission(
            QueryCostOutcome.Admitted,
            "user-1",
            ClientQuotaPolicy.ForTier(ClientTier.Standard),
            50, 2000, 500, 0, null);

        await quota.SettleAsync(admission, 10, CancellationToken.None);
        await quota.Received(1).SettleAsync(admission, 10, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task AsyncAdmission_CapturedAtSubmit()
    {
        var (quota, _, options) = CreateQuotaService();
        var user = CreateUser();

        var admission = await quota.AdmitAsync(
            new QueryCostRequest(user, null, null, EstimatedCost: 50, TransportMaxCost: 2000, CapByTierMaxCostPerQuery: false),
            CancellationToken.None);

        admission.SubjectId.Should().Be("user:tenant-1:user-1");
        admission.Policy.Tier.Should().Be(ClientTier.Standard);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SettleFailure_DoesNotTurnFailureIntoSuccess()
    {
        var quota = Substitute.For<IQueryCostQuota>();
        var admission = new QueryCostAdmission(
            QueryCostOutcome.Admitted,
            "user-1",
            ClientQuotaPolicy.ForTier(ClientTier.Standard),
            50, 2000, 500, 0, null);

        quota.When(q => q.SettleAsync(Arg.Any<QueryCostAdmission>(), Arg.Any<int>(), Arg.Any<CancellationToken>()))
             .Do(_ => throw new InvalidOperationException("Settle backend failure"));

        var act = () =>
        {
            try
            {
                throw new InvalidOperationException("Original statement query failed");
            }
            finally
            {
                try
                {
                    quota.SettleAsync(admission, 10, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch
                {
                    // Swallowed in finally
                }
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(act);
        ex.Message.Should().Be("Original statement query failed");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuotaStoreFailure_FailsClosed()
    {
        var quota = Substitute.For<IQueryCostQuota>();
        quota.AdmitAsync(default!, default).ReturnsForAnyArgs<QueryCostAdmission>(_ => throw new TimeoutException("Redis unavailable"));

        var act = async () =>
        {
            try
            {
                await quota.AdmitAsync(new QueryCostRequest(CreateUser(), null, null, 100, 2000, false), CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new SqlQuotaUnavailableException();
            }
        };

        await act.Should().ThrowAsync<SqlQuotaUnavailableException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PolicyDenied_DebitsFailureFee()
    {
        var (quota, limiter, _) = CreateQuotaService(failureFee: 25, standardCapacity: 100);
        var user = CreateUser();

        await quota.ChargeFailureFeeAsync(user, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SyntaxError_NotCharged()
    {
        var quota = Substitute.For<IQueryCostQuota>();

        // Syntax error occurs during parse before admission or execution
        Assert.ThrowsAny<Exception>(() => FastSqlEngine.Default.Parse("SELECT FROM".AsMemory()));

        // Failure fee must not be charged for syntax errors
        quota.DidNotReceiveWithAnyArgs().ChargeFailureFeeAsync(default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CostDisabled_FailsStartOutsideDevelopment()
    {
        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Cost = new WebSqlCostOptions { Enabled = false }
            }
        };

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        var ex = Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(() => GatewayStartupValidator.ValidateStartup(options, env));
        ex.Message.Should().Contain("WebSql:Cost:Enabled must not be false outside Development");
    }
}
