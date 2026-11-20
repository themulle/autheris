namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.State.Caching.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Analysis;
using Autheris.Infrastructure.RateLimiting;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestSupport;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #492: Query analysis with optional <c>targetScope</c> evaluates cost limits and <c>rowsPerRequest</c>
/// against the allowlisted read-only scope of the same person without mutating any rate-limit bucket or reading source rows.
/// </summary>
public sealed class QueryAnalysisTargetScopeTests
{
    public sealed class OrderRow
    {
        public string Id { get; set; } = "1";
        public string Region { get; set; } = "EU";
    }

    public sealed class TargetScopeQuery
    {
        public List<OrderRow> GetOrders(int first = 10) => [new()];
    }

    private static ClaimsPrincipal CreateCallerPrincipal(string sid, string scope, string role = "Analyst")
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, sid),
            new(ClaimTypes.Name, sid),
            new("sid", sid),
            new(ClaimTypes.Role, role),
            new("scp", scope),
            DefaultTenantCaller.Claim
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static (GatewayOptions GatewayOptions, QueryAnalysisSettings QueryAnalysisSettings) CreateGatewayOptions(params string[] allowedTargetScopes) => (
        new GatewayOptions
        {
            RateLimiting = new RateLimitingOptions
            {
                ClientTiers = new ClientTierOptions
                {
                    TierLimits = new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Standard"] = new() { MaxCostPerQuery = 5000, MaxTokensCapacity = 10000, TokenRefillRatePerSecond = 0 },
                        ["Enterprise"] = new() { MaxCostPerQuery = 10000, MaxTokensCapacity = 20000, TokenRefillRatePerSecond = 0 }
                    },
                    RoleTierMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Enterprise"] = "Enterprise"
                    },
                    ScopeTierMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Agent.Read"] = "Standard",
                        ["Apps.Read"] = "Enterprise"
                    },
                    ScopeLimits = new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Agent.Read"] = new() { MaxCostPerQuery = 2500, MaxTokensCapacity = 5000 }
                    }
                }
            }
        },
        new QueryAnalysisSettings
        {
            AllowedTargetScopes = [.. allowedTargetScopes]
        });

    private static async Task<IGraphQlCostAnalysisEngine> CreateCostEngineAsync()
    {
        var services = new ServiceCollection();
        services.AddGraphQLServer().AddQueryType<TargetScopeQuery>();
        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IRequestExecutorProvider>();
        await provider.GetExecutorAsync();
        return new HotChocolateGraphQlCostAnalysisEngine(provider);
    }

    private static (QueryAnalysisService Service, InMemoryRateLimiterService Limiter, TableMetadata Table) CreateService(
        (GatewayOptions GatewayOptions, QueryAnalysisSettings QueryAnalysisSettings) config,
        IGraphQlCostAnalysisEngine? engine = null)
    {
        var tableId = new TableIdentifier("sales", "public", "orders");
        var table = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "sales",
                SchemaName = "public",
                TableName = "orders",
                DisplayName = "orders"
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "varchar" },
                new TableColumn { ColumnName = "region", DataType = "varchar" }
            ],
            PrimaryKeyColumns = ["id"]
        };

        var metadataRepo = CatalogVisibilityTestFactory.ServeCatalogReads(Substitute.For<ITableMetadataRepository>());
        metadataRepo.StubAllTables([table]);

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns([new Consent
            {
                TableIdentifier = tableId,
                Effect = ConsentEffect.Allow,
                GranteeSid = new Sid("usr-1"),
                TenantId = TestTenants.Default,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(5)
            }]);

        var opts = Options.Create(config.GatewayOptions);
        var tierResolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, opts);
        var limiter = new InMemoryRateLimiterService();

        var service = new QueryAnalysisService(
            metadataRepo,
            consentRepo,
            new InMemoryAuditLogRepository(),
            gatewayOptions: opts,
            costAnalysisEngine: engine,
            clientTierResolver: tierResolver,
            rateLimiterService: limiter,
            queryAnalysisSettings: Options.Create(config.QueryAnalysisSettings));

        return (service, limiter, table);
    }

    [Fact]
    public async Task AnalyzeQueriesAsync_WithAllowlistedTargetScope_EvaluatesAgainstTargetScopeLimitWithoutMutatingBuckets()
    {
        var engine = await CreateCostEngineAsync();
        var gwOpts = CreateGatewayOptions("Apps.Read");
        var (service, limiter, _) = CreateService(gwOpts, engine);

        var caller = CreateCallerPrincipal("usr-1", "Agent.Read", role: "Enterprise");
        var tierResolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(gwOpts.GatewayOptions));
        var callerQuota = await tierResolver.ResolveAsync(caller, null, null);
        await limiter.CheckCostQuotaAsync(callerQuota.SubjectId, 500, callerQuota.Policy);
        var bucketBefore = await limiter.GetBucketStateAsync(callerQuota.SubjectId, callerQuota.Policy);
        bucketBefore.ShouldNotBeNull();

        // Query with first: 500 -> cost 6000 (> 2500 Agent.Read limit and <= 10000 Apps.Read Enterprise limit).
        var query = QueryAnalysisItem.FromGraphQl("query { orders(first: 500) { id region } }");

        var withTargetScope = await service.AnalyzeQueriesAsync([query], caller, targetPrincipal: null, targetScope: "Apps.Read");
        withTargetScope.ValidationReport.CostEstimate.EstimatedCost.ShouldBeGreaterThan(2500);
        withTargetScope.ValidationReport.CostEstimate.EstimatedCost.ShouldBeLessThanOrEqualTo(10000);
        withTargetScope.ValidationReport.CostEstimate.MaxAllowedCost.ShouldBe(10000);
        withTargetScope.ValidationReport.CostEstimate.IsWithinCostLimit.ShouldBeTrue();
        withTargetScope.ValidationReport.CostEstimate.SecondsAtCurrentBucket.ShouldBeNull();

        var withoutTargetScope = await service.AnalyzeQueriesAsync([query], caller);
        withoutTargetScope.ValidationReport.CostEstimate.MaxAllowedCost.ShouldBe(2500);
        withoutTargetScope.ValidationReport.CostEstimate.IsWithinCostLimit.ShouldBeFalse();

        // Caller's own bucket state remains unchanged.
        var bucketAfter = await limiter.GetBucketStateAsync(callerQuota.SubjectId, callerQuota.Policy);
        bucketAfter!.CurrentBalance.ShouldBe(bucketBefore!.CurrentBalance);
    }

    [Fact]
    public async Task AnalyzeQueriesAsync_RowsPerRequestUnderTargetScope_MatchesExecutionAdmissionBoundary()
    {
        var engine = await CreateCostEngineAsync();
        var gwOpts = CreateGatewayOptions("Apps.Read");
        var (service, _, _) = CreateService(gwOpts, engine);

        var caller = CreateCallerPrincipal("usr-1", "Agent.Read", role: "Enterprise");
        var largeQuery = QueryAnalysisItem.FromGraphQl("query { orders(first: 5000) { id region } }");

        var analysis = await service.AnalyzeQueriesAsync([largeQuery], caller, targetPrincipal: null, targetScope: "Apps.Read");
        analysis.ValidationReport.CostEstimate.MaxAllowedCost.ShouldBe(10000);
        var rowsPerRequest = analysis.ValidationReport.CostEstimate.RowsPerRequest;
        rowsPerRequest.ShouldNotBeNull();
        rowsPerRequest.Value.ShouldBeGreaterThan(0);

        // Parity check: resolve Apps.Read quota policy as execution would and verify admission at rowsPerRequest vs above.
        var appsPrincipal = CreateCallerPrincipal("usr-1", "Apps.Read", role: "Enterprise");
        var tierResolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(gwOpts.GatewayOptions));
        var execQuota = await tierResolver.ResolveAsync(appsPrincipal, null, null);
        execQuota.Policy.MaxCostPerQuery.ShouldBe(10000);

        var admittedAnalysis = await service.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromGraphQl($"query {{ orders(first: {rowsPerRequest.Value}) {{ id region }} }}")],
            appsPrincipal);
        admittedAnalysis.ValidationReport.CostEstimate.IsWithinCostLimit.ShouldBeTrue();
        admittedAnalysis.ValidationReport.CostEstimate.EstimatedCost.ShouldBeLessThanOrEqualTo(execQuota.Policy.MaxCostPerQuery);

        var overAnalysis = await service.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromGraphQl($"query {{ orders(first: {rowsPerRequest.Value + 100}) {{ id region }} }}")],
            appsPrincipal);
        overAnalysis.ValidationReport.CostEstimate.IsWithinCostLimit.ShouldBeFalse();
    }

    [Fact]
    public async Task AnalyzeQueriesAsync_DisallowedTargetScope_IsRejectedWithValidationErrorAndNoEstimate()
    {
        var gwOpts = CreateGatewayOptions("Apps.Read");
        var (service, _, _) = CreateService(gwOpts);
        var caller = CreateCallerPrincipal("usr-1", "Agent.Read", role: "Enterprise");
        var query = QueryAnalysisItem.FromGraphQl("query { orders(first: 10) { id } }");

        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            service.AnalyzeQueriesAsync([query], caller, targetPrincipal: null, targetScope: "Admin.All"));
        ex.Message.ShouldContain("AllowedTargetScopes");

        await Should.ThrowAsync<ArgumentException>(() =>
            service.AnalyzeQueriesAsync([query], caller, targetPrincipal: null, targetScope: "   "));

        // Endpoint returns 400 Bad Request with INVALID_TARGET_SCOPE and no estimate.
        var httpContext = new DefaultHttpContext();
        httpContext.User = caller;
        var httpResult = await QueryAnalysisAndProfilingEndpoints.HandleAnalyzeQueriesAsync(
            new AnalyzeQueriesHttpRequest([query], TargetScope: "Admin.All"),
            httpContext,
            service,
            CancellationToken.None);

        var badRequest = httpResult.ShouldBeOfType<ProblemHttpResult>();
        badRequest.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }
}
