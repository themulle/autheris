namespace Autheris.Tests.Unit.RateLimiting;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Common.Quota;
using Autheris.Application.DataSources;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.OData;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Application.State.Caching.Interfaces;
using Autheris.Application.State.Caching.Services;
using Autheris.Application.Tenancy;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Mcp;
using Autheris.Infrastructure.RateLimiting;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestDoubles;
using Autheris.Tests.Unit.TestSupport;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using TrinoSqlEngine;
using Xunit;
using DomainRequestContext = Autheris.Domain.Model.RequestContext;

[Trait("Category", "Security")]
public sealed class ScopeQueryExecutionGuardPipelineTests : IDisposable
{
    public ScopeQueryExecutionGuardPipelineTests()
    {
        BlockingGraphQlQuery.Reset();
        CatalogRelationRegistry.Clear();
    }

    public void Dispose()
    {
        BlockingGraphQlQuery.Reset();
        CatalogRelationRegistry.Clear();
    }

    [Fact]
    public async Task GraphQl_CostAndQuotaMiddleware_EnforcesScopeConcurrencyLimit_AcrossConcurrentRequests()
    {
        var options = ScopeQueryExecutionGuardTests.CreateGatewayOptions(scopeLimits: new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent.Read"] = new() { MaxConcurrentQueries = 2 }
        });
        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(options));
        var guard = new ScopeQueryExecutionGuard(resolver);
        var accessor = new AsyncLocalHttpContextAccessor();

        var services = new ServiceCollection();
        services.AddSingleton<IRateLimiterService>(AllowAllRateLimiterService.Instance);
        services.AddSingleton<IHttpContextAccessor>(accessor);
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<IClientTierResolver>(resolver);
        services.AddSingleton<IQueryCostQuota, QueryCostQuota>();
        services.AddSingleton<IScopeQueryExecutionGuard>(guard);

        var executor = await services
            .AddGraphQLServer()
            .AddQueryType<BlockingGraphQlQuery>()
            .UseRequest<CostAndQuotaMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();

        var agentPrincipal = ScopeQueryExecutionGuardTests.CreatePrincipal("S-1-5-21-GQL-USER", scp: "Agent.Read");
        var dataPrincipal = ScopeQueryExecutionGuardTests.CreatePrincipal("S-1-5-21-GQL-USER", scp: "Data.Read");

        async Task<(int Status, string? ScopeHeader, IExecutionResult Result)> RunGraphQlAsync(ClaimsPrincipal user)
        {
            var httpCtx = new DefaultHttpContext { User = user };
            accessor.HttpContext = httpCtx;
            var res = await executor.ExecuteAsync(OperationRequestBuilder.New().SetDocument("{ waitForRelease }").Build());
            return (httpCtx.Response.StatusCode, httpCtx.Response.Headers["X-RateLimit-Scope"].FirstOrDefault(), res);
        }

        var inFlight1 = RunGraphQlAsync(agentPrincipal);
        var inFlight2 = RunGraphQlAsync(agentPrincipal);
        await BlockingGraphQlQuery.WaitUntilEnteredAsync(2);

        // 3rd concurrent request for Agent.Read fails at CostAndQuotaMiddleware with 429 SCOPE_CONCURRENCY_EXCEEDED and zero scope leak.
        var (status3, scopeHeader3, res3) = await RunGraphQlAsync(agentPrincipal);
        status3.ShouldBe(StatusCodes.Status429TooManyRequests);
        scopeHeader3.ShouldBeNull();
        var op3 = res3.ShouldBeOfType<OperationResult>();
        op3.Errors.ShouldNotBeNull();
        op3.Errors[0].Code.ShouldBe(ApiErrorCodes.ScopeConcurrencyExceeded);
        var ext3 = op3.Errors[0].Extensions;
        (ext3 is null || !ext3.ContainsKey("scope")).ShouldBeTrue();

        // Same user with Data.Read is unaffected.
        var otherCtx = new DefaultHttpContext { User = dataPrincipal };
        accessor.HttpContext = otherCtx;
        var resOther = await executor.ExecuteAsync(OperationRequestBuilder.New().SetDocument("{ ping }").Build());
        otherCtx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var otherErrors = resOther.ShouldBeOfType<OperationResult>().Errors;
        (otherErrors is null || otherErrors.Count == 0).ShouldBeTrue();

        BlockingGraphQlQuery.ReleaseAll();
        var (status1, _, _) = await inFlight1;
        var (status2, _, _) = await inFlight2;
        status1.ShouldBe(StatusCodes.Status200OK);
        status2.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task GraphQl_CostAndQuotaMiddleware_MaxQueryDurationSeconds_Returns504Timeout()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 6, 0, 0, TimeSpan.Zero));
        var options = ScopeQueryExecutionGuardTests.CreateGatewayOptions(scopeLimits: new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent.Read"] = new() { MaxQueryDurationSeconds = 5 }
        });
        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(options));
        var guard = new ScopeQueryExecutionGuard(resolver, time);
        var accessor = new AsyncLocalHttpContextAccessor();

        var services = new ServiceCollection();
        services.AddSingleton<IRateLimiterService>(AllowAllRateLimiterService.Instance);
        services.AddSingleton<IHttpContextAccessor>(accessor);
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<IClientTierResolver>(resolver);
        services.AddSingleton<IQueryCostQuota, QueryCostQuota>();
        services.AddSingleton<IScopeQueryExecutionGuard>(guard);

        var executor = await services
            .AddGraphQLServer()
            .AddQueryType<BlockingGraphQlQuery>()
            .UseRequest<CostAndQuotaMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();

        var principal = ScopeQueryExecutionGuardTests.CreatePrincipal("S-1-5-21-GQL-TIMEOUT", scp: "Agent.Read");
        var httpCtx = new DefaultHttpContext { User = principal };
        accessor.HttpContext = httpCtx;

        var running = executor.ExecuteAsync(OperationRequestBuilder.New().SetDocument("{ waitForHttpAbort }").Build());
        await BlockingGraphQlQuery.WaitUntilEnteredAsync(1);
        time.Advance(TimeSpan.FromSeconds(5));

        var op = (await running).ShouldBeOfType<OperationResult>();
        httpCtx.Response.StatusCode.ShouldBe(StatusCodes.Status504GatewayTimeout);
        op.Errors.ShouldNotBeNull();
        op.Errors[0].Code.ShouldBe("TIMEOUT");
    }

    [Fact]
    public async Task WebSql_GovernedSqlExecutionService_ClampsCommandTimeoutAndRows_AndBlocksConcurrentOverflow()
    {
        var options = ScopeQueryExecutionGuardTests.CreateGatewayOptions(
            scopeLimits: new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["Agent.Read"] = new()
                {
                    MaxConcurrentQueries = 1,
                    MaxQueryDurationSeconds = 5,
                    MaxResultRows = 3
                }
            },
            maxResultRowsGlobal: 500);

        var optsWrapper = Options.Create(options);
        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, optsWrapper);
        var guard = new ScopeQueryExecutionGuard(resolver);

        var tableRepo = new InMemoryTableMetadataRepository(ScopeQueryExecutionGuardTests.CreateOrdersMetadata());
        var consentResolution = new StubConsentResolutionService(table =>
            TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase), hasUnconstrainedColumnAllow: true));

        var recordedTimeouts = new List<int>();
        var enteredDb = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDb = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockOnRead = false;

        var connFactory = new RecordingSqlConnectionFactory(
            onExecute: timeout => recordedTimeouts.Add(timeout),
            onWaitAsync: async ct =>
            {
                if (blockOnRead)
                {
                    enteredDb.TrySetResult();
                    await releaseDb.Task.WaitAsync(ct);
                }
            },
            totalRows: 10);

        var sqlService = CreateRealSqlService(
            tableRepo, consentResolution, optsWrapper, guard, connFactory);

        var principal = ScopeQueryExecutionGuardTests.CreatePrincipal(scp: "Agent.Read");
        var tenant = new TenantId("tenant-1");

        // 1. Clamps CommandTimeout from 30s -> 5s and result rows from 500 -> 3.
        var buffered = await sqlService.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("SELECT id, name FROM sales.public.orders ORDER BY id", RowLimit: SqlRowLimit.For(options.WebSql), Transport: SqlTransports.WebSql),
            principal,
            tenant);

        recordedTimeouts.ShouldBe([5]);
        buffered.Rows.Count.ShouldBe(3);

        // 2. Concurrent WebSQL execution hits MaxConcurrentQueries = 1 and maps to 429 SCOPE_CONCURRENCY_EXCEEDED.
        blockOnRead = true;
        var active = sqlService.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("SELECT id, name FROM sales.public.orders ORDER BY id", Transport: SqlTransports.WebSql),
            principal,
            tenant);
        await enteredDb.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var overflow = await Should.ThrowAsync<ScopeConcurrencyExceededException>(() =>
            sqlService.ExecuteQueryBufferedAsync(
                new GovernedSqlQueryRequest("SELECT id, name FROM sales.public.orders ORDER BY id", Transport: SqlTransports.WebSql),
                principal,
                tenant));
        SqlFailureClassifier.Classify(overflow).Code.ShouldBe(ApiErrorCodes.ScopeConcurrencyExceeded);

        releaseDb.TrySetResult();
        await active;
    }

    [Fact]
    public async Task RestData_OData_And_Mcp_EnforceScopeRowCap_AndScopeConcurrencyExceeded()
    {
        var options = ScopeQueryExecutionGuardTests.CreateGatewayOptions(
            scopeLimits: new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["Agent.Read"] = new()
                {
                    MaxConcurrentQueries = 1,
                    MaxResultRows = 3
                }
            },
            maxResultRowsGlobal: 500);

        var optsWrapper = Options.Create(options);
        var optsMonitor = new StaticOptionsMonitor<GatewayOptions>(options);
        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, optsWrapper);
        var guard = new ScopeQueryExecutionGuard(resolver);

        var metadata = ScopeQueryExecutionGuardTests.CreateOrdersMetadata();
        var metaRepo = new InMemoryTableMetadataRepository(metadata);
        var allowAllDecision = TableAccessDecision.Allowed(ScopeQueryExecutionGuardTests.OrdersTableId, new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase), hasUnconstrainedColumnAllow: true);
        var pdp = AccessDecisionPointStubs.Returning(_ => allowAllDecision);
        var principal = ScopeQueryExecutionGuardTests.CreatePrincipal(scp: "Agent.Read");
        var tenant = new TenantId("tenant-1");

        // 1. REST Data: GovernedDataQueryService clamps Limit=500 -> RowLimit 3 & propagates ScopeConcurrencyExceededException.
        var sqlStub = new StubGovernedSqlExecutionService();
        var dataService = new GovernedDataQueryService(
            metaRepo,
            pdp,
            sqlStub,
            StubFederatedQueryExecutionService.Empty,
            StubColumnMaskingProvider.Null,
            optsWrapper,
            scopeGuard: guard);

        var dataEnvelope = await dataService.ExecuteQueryAsync(
            new DatasetQueryRequest(ScopeQueryExecutionGuardTests.OrdersTableId, Limit: 500, Offset: 0),
            new DomainRequestContext(principal, tenant));

        dataEnvelope.Limit.ShouldBe(3);
        sqlStub.Requests.Count.ShouldBe(1);
        sqlStub.Requests[0].Sql.ShouldContain("LIMIT 4");

        var heldGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldTask = guard.RunAsync(principal, null, null, async (_, _) =>
        {
            await heldGate.Task;
            return true;
        });

        await Should.ThrowAsync<ScopeConcurrencyExceededException>(() =>
            dataService.ExecuteQueryAsync(new DatasetQueryRequest(ScopeQueryExecutionGuardTests.OrdersTableId, Limit: 10, Offset: 0), new DomainRequestContext(principal, tenant)));

        heldGate.TrySetResult();
        await heldTask;

        // 2. OData: ODataHandler clamps top=100 -> 3 (First = scopedTop + 1 = 4) and maps ScopeConcurrencyExceededException to HTTP 429 SCOPE_CONCURRENCY_EXCEEDED.
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10 * 1024 * 1024 });
        var gwStub = StubGatewayExecutionService.WithPage(new TableQueryPage([], allowAllDecision, null, false));
        var odataHandler = new ODataHandler(
            metaRepo,
            gwStub,
            NullLogger<ODataHandler>.Instance,
            new TestHostEnvironment("Production"),
            memoryCache,
            StaticCatalogVersionProvider.Instance,
            new TenantResolver(() => new TenancyOptions(), null, NullLogger<TenantResolver>.Instance),
            PermissiveDataSourceOptionsResolver.Instance,
            new IngressTransportContext(),
            new DelegatingODataQueryPolicyEnforcer((_, _, _, _) => ValueTask.FromResult(allowAllDecision)))
        {
            ScopeGuard = guard
        };

        var odataOk = await odataHandler.ExecuteEntitySetQueryAsync(
            principal, "https://gateway.example/odata/v4", ScopeQueryExecutionGuardTests.OrdersTableId,
            top: 100, skip: 0, select: null, includeCount: false, headers: null, orderBy: null, filter: null);
        odataOk.Success.ShouldBeTrue();
        gwStub.LastPageRequest.ShouldNotBeNull();
        gwStub.LastPageRequest!.First.ShouldBe(4);

        var odataGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var odataHeld = guard.RunAsync(principal, null, null, async (_, _) => { await odataGate.Task; return true; });

        var odataThrottled = await odataHandler.ExecuteEntitySetQueryAsync(
            principal, "https://gateway.example/odata/v4", ScopeQueryExecutionGuardTests.OrdersTableId,
            top: 10, skip: 0, select: null, includeCount: false, headers: null, orderBy: null, filter: null);
        odataThrottled.Success.ShouldBeFalse();
        odataThrottled.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        odataThrottled.ErrorCode.ShouldBe(ApiErrorCodes.ScopeConcurrencyExceeded);

        odataGate.TrySetResult();
        await odataHeld;

        // 3. MCP: McpToolExecutionHandler & GatewayMcpQueryExecutor clamp query_sql limit and surface SCOPE_CONCURRENCY_EXCEEDED.
        var mcpHandler = new McpToolExecutionHandler(
            sqlStub,
            dataService,
            new StubCatalogDiscoveryService(),
            new StubMcpApiDispatcherService(),
            new InMemoryLineageGraphStore(),
            metaRepo,
            gatewayOptions: optsWrapper,
            logger: NullLogger<McpToolExecutionHandler>.Instance,
            scopeGuard: guard);

        var session = new McpSessionContext(
            "sess-1053", "spn-1", "tenant-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            UserSid: "S-1-5-21-MCP-1053",
            Roles: ["Consumer"],
            GroupSids: [],
            AdditionalClaims: new Dictionary<string, string> { ["scp"] = "Agent.Read" });

        var querySqlTool = McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.QuerySql);
        await mcpHandler.ExecuteToolAsync(querySqlTool, """{"query":"SELECT id, name FROM sales.public.orders ORDER BY id"}""", session);
        sqlStub.Requests[^1].RowLimit.ShouldNotBeNull();
        sqlStub.Requests[^1].RowLimit!.Effective(50).ShouldBe(3);

        var mcpPrincipal = ScopeQueryExecutionGuardTests.CreatePrincipal("S-1-5-21-MCP-1053", scp: "Agent.Read", clientId: "spn-1");
        var mcpGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mcpHeld = guard.RunAsync(mcpPrincipal, null, null, async (_, _) => { await mcpGate.Task; return true; });

        var throttledSqlJson = await mcpHandler.ExecuteToolAsync(querySqlTool, """{"query":"SELECT id FROM sales.public.orders ORDER BY id"}""", session);
        Autheris.Tests.Unit.Mcp.McpErrorEnvelopeAssert.Error(throttledSqlJson);
        throttledSqlJson.ShouldContain(ApiErrorCodes.ScopeConcurrencyExceeded);

        var mcpExecutor = new GatewayMcpQueryExecutor(
            new FixedExecutorProvider(new StubGraphQlExecutor()),
            gwStub,
            NullLogger<GatewayMcpQueryExecutor>.Instance,
            optsMonitor,
            new PersistedToolValidator(),
            toolExecutionHandler: mcpHandler,
            scopeGuard: guard);

        var throttledGqlJson = await mcpExecutor.ExecuteOperationAsync(
            new McpToolDefinition("query_graphql", "GQL", """{"type":"object","properties":{"query":{"type":"string"}}}""", ""),
            """{"query":"{ ping }"}""",
            session);
        Autheris.Tests.Unit.Mcp.McpErrorEnvelopeAssert.Error(throttledGqlJson);
        throttledGqlJson.ShouldContain(ApiErrorCodes.ScopeConcurrencyExceeded);

        mcpGate.TrySetResult();
        await mcpHeld;

        // 4. Real SQL pipeline with 10 rows and MaxResultRows=3 keeps probe row cleanly across REST Data (HasMore=true) and OData (@odata.nextLink).
        var realSql = CreateRealSqlService(
            metaRepo,
            new StubConsentResolutionService(_ => allowAllDecision),
            optsWrapper,
            guard,
            new RecordingSqlConnectionFactory(_ => { }, _ => Task.CompletedTask, totalRows: 10));

        var realRestData = new GovernedDataQueryService(metaRepo, pdp, realSql, StubFederatedQueryExecutionService.Empty, StubColumnMaskingProvider.Null, optsWrapper, scopeGuard: guard);
        var realRestResult = await realRestData.ExecuteQueryAsync(new DatasetQueryRequest(ScopeQueryExecutionGuardTests.OrdersTableId, Limit: 500, Offset: 0), new DomainRequestContext(principal, tenant));
        realRestResult.Limit.ShouldBe(3);
        realRestResult.Data.Count.ShouldBe(3);
        realRestResult.HasMore.ShouldBeTrue();

        var realGw = new GatewayExecutionService(
            metaRepo,
            new InMemoryConsentRepository(),
            new InMemoryAuditLogRepository(),
            new StubConsentResolutionService(_ => allowAllDecision),
            new InMemoryConsentCacheService(),
            StubColumnMaskingProvider.Null,
            optsWrapper,
            dataSourceExecutors: [new SqlTableReadExecutor(new GovernedTableReadPlanner(), () => realSql, optsWrapper)],
            accessDecisionPoint: pdp,
            transportGate: AllowAllTransportGate.Instance);
        var realOdata = new ODataHandler(
            metaRepo,
            realGw,
            NullLogger<ODataHandler>.Instance,
            new TestHostEnvironment("Production"),
            memoryCache,
            StaticCatalogVersionProvider.Instance,
            new TenantResolver(() => new TenancyOptions(), null, NullLogger<TenantResolver>.Instance),
            PermissiveDataSourceOptionsResolver.Instance,
            new IngressTransportContext(),
            new DelegatingODataQueryPolicyEnforcer((_, _, _, _) => ValueTask.FromResult(allowAllDecision)))
        {
            ScopeGuard = guard
        };
        var odataRealPage = await realOdata.ExecuteEntitySetQueryAsync(
            principal, "https://gateway.example/odata/v4", ScopeQueryExecutionGuardTests.OrdersTableId,
            top: 100, skip: 0, select: null, includeCount: false, headers: null, orderBy: null, filter: null);
        odataRealPage.Success.ShouldBeTrue();
        System.Text.Json.JsonSerializer.Serialize(odataRealPage.Payload).ShouldContain("@odata.nextLink");

        // 5. MCP query_sql & query_graphql scope timeout maps cleanly to QUERY_TIMEOUT instead of unhandled exception.
        var timeoutTime = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 6, 0, 0, TimeSpan.Zero));
        var timeoutOptions = ScopeQueryExecutionGuardTests.CreateGatewayOptions(scopeLimits: new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent.Read"] = new() { MaxQueryDurationSeconds = 5 }
        });
        var timeoutGuard = new ScopeQueryExecutionGuard(new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(timeoutOptions)), timeoutTime);
        var slowSqlStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowSqlHandler = new McpToolExecutionHandler(
            new DelayingSqlExecutionService(slowSqlStarted),
            dataService,
            new StubCatalogDiscoveryService(),
            new StubMcpApiDispatcherService(),
            new InMemoryLineageGraphStore(),
            metaRepo,
            gatewayOptions: Options.Create(timeoutOptions),
            logger: NullLogger<McpToolExecutionHandler>.Instance,
            scopeGuard: timeoutGuard);
        var timedSqlTask = slowSqlHandler.ExecuteToolAsync(querySqlTool, """{"query":"SELECT id FROM sales.public.orders ORDER BY id"}""", session);
        await slowSqlStarted.Task;
        timeoutTime.Advance(TimeSpan.FromSeconds(5));
        var timedSqlJson = await timedSqlTask;
        Autheris.Tests.Unit.Mcp.McpErrorEnvelopeAssert.Error(timedSqlJson);
        timedSqlJson.ShouldContain("QUERY_TIMEOUT");

        var slowGqlStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutExecutor = new GatewayMcpQueryExecutor(
            new FixedExecutorProvider(new DelayingGraphQlExecutor(slowGqlStarted)),
            gwStub,
            NullLogger<GatewayMcpQueryExecutor>.Instance,
            new StaticOptionsMonitor<GatewayOptions>(timeoutOptions),
            new PersistedToolValidator(),
            toolExecutionHandler: slowSqlHandler,
            scopeGuard: timeoutGuard);
        var timedGqlTask = timeoutExecutor.ExecuteOperationAsync(
            new McpToolDefinition("query_graphql", "GQL", """{"type":"object","properties":{"query":{"type":"string"}}}""", ""),
            """{"query":"{ ping }"}""",
            session);
        await slowGqlStarted.Task;
        timeoutTime.Advance(TimeSpan.FromSeconds(5));
        var timedGqlJson = await timedGqlTask;
        Autheris.Tests.Unit.Mcp.McpErrorEnvelopeAssert.Error(timedGqlJson);
        timedGqlJson.ShouldContain("QUERY_TIMEOUT");
    }

    private static GovernedSqlExecutionService CreateRealSqlService(
        ITableMetadataRepository tableRepo,
        IConsentResolutionService consentResolution,
        IOptions<GatewayOptions> optsWrapper,
        IScopeQueryExecutionGuard guard,
        ISqlConnectionFactory connFactory)
    {
        var env = new TestHostEnvironment("Development");
        var rewrite = new GovernedSqlRewriteDependencies(
            consentResolution,
            tableRepo,
            NullClientIpResolver.Instance,
            new InMemoryConsentRepository(),
            FastSqlEngine.Default,
            GovernedTestSupport.PolicyFactory(tableRepo, optsWrapper),
            TypedPolicies: GovernedTestSupport.TypedPolicies(optsWrapper));
        var execDeps = new GovernedSqlExecutionDependencies(
            connFactory,
            env,
            AuditDigester: new CompiledSqlAuditDigester(optsWrapper, null, env),
            ScopeGuard: guard);
        return new GovernedSqlExecutionService(
            optsWrapper,
            new InMemoryAuditWriter(),
            GovernedTestSupport.Guard,
            rewrite,
            execDeps,
            logger: NullLogger<GovernedSqlExecutionService>.Instance);
    }

    private sealed class AsyncLocalHttpContextAccessor : IHttpContextAccessor
    {
        private static readonly AsyncLocal<HttpContext?> Current = new();
        public HttpContext? HttpContext { get => Current.Value; set => Current.Value = value; }
    }

    private sealed class AllowAllRateLimiterService : IRateLimiterService
    {
        public static AllowAllRateLimiterService Instance { get; } = new();
        public Task<RateLimitResult> CheckPreAuthIpAsync(string ip, PreAuthIpRateLimitOptions options, CancellationToken ct = default)
            => Task.FromResult(new RateLimitResult(true, 0));
        public Task<RateLimitResult> CheckPostAuthSidAsync(string sid, PostAuthSidRateLimitOptions options, CancellationToken ct = default)
            => Task.FromResult(new RateLimitResult(true, 0));
        public Task<CostQuotaResult> CheckCostQuotaAsync(string key, int requestedCost, ClientQuotaPolicy policy, CancellationToken ct = default)
            => Task.FromResult(new CostQuotaResult(true, 100, 0));
        public Task<CostQuotaResult> ReserveCostQuotaAsync(string key, int reservedCost, ClientQuotaPolicy policy, CancellationToken ct = default)
            => Task.FromResult(new CostQuotaResult(true, 100, 0));
        public Task<CostQuotaResult> SettleCostQuotaAsync(string key, int reservedCost, int actualCost, ClientQuotaPolicy policy, CancellationToken ct = default)
            => Task.FromResult(new CostQuotaResult(true, 90, 0));
        public Task<CallerBucketState?> GetBucketStateAsync(string key, ClientQuotaPolicy policy, CancellationToken ct = default)
            => Task.FromResult<CallerBucketState?>(null);
        public Task<CallerBucketState?> GetBucketStateAsync(string key, CancellationToken ct = default)
            => Task.FromResult<CallerBucketState?>(null);
    }

    public sealed class BlockingGraphQlQuery
    {
        private static int _entered;
        private static TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static void Reset()
        {
            Volatile.Write(ref _entered, 0);
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public static async Task WaitUntilEnteredAsync(int expectedCount)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref _entered) < expectedCount && DateTime.UtcNow < deadline)
            {
                await Task.Yield();
            }

            Volatile.Read(ref _entered).ShouldBeGreaterThanOrEqualTo(expectedCount);
        }

        public static void ReleaseAll() => _gate.TrySetResult();

        public async Task<string> WaitForRelease(CancellationToken ct)
        {
            Interlocked.Increment(ref _entered);
            await _gate.Task.WaitAsync(ct);
            return "released";
        }

        public async Task<string> WaitForHttpAbort([Service] IHttpContextAccessor httpAccessor)
        {
            Interlocked.Increment(ref _entered);
            await _gate.Task.WaitAsync(httpAccessor.HttpContext!.RequestAborted);
            return "released";
        }

        public string Ping() => "pong";
    }

    private sealed class RecordingSqlConnectionFactory(Action<int> onExecute, Func<CancellationToken, Task> onWaitAsync, int totalRows) : ISqlConnectionFactory
    {
        public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
            => Task.FromResult<DbConnection>(new FakeSqlConnection(onExecute, onWaitAsync, totalRows));
    }

    private sealed class FakeSqlConnection(Action<int> onExecute, Func<CancellationToken, Task> onWaitAsync, int totalRows) : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "sales";
        public override string DataSource => "localhost";
        public override string ServerVersion => "16.0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeDbTransaction(this);
        protected override DbCommand CreateDbCommand() => new FakeDbCommand(this, onExecute, onWaitAsync, totalRows);
    }

    private sealed class FakeDbCommand(DbConnection connection, Action<int> onExecute, Func<CancellationToken, Task> onWaitAsync, int totalRows) : DbCommand
    {
        private readonly FakeParamCollection _params = new();
        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; } = 30;
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbParameterCollection DbParameterCollection => _params;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => 0;
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new FakeDbParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            onExecute(CommandTimeout);
            return new FakeRowsReader(totalRows);
        }

        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            onExecute(CommandTimeout);
            await onWaitAsync(cancellationToken).ConfigureAwait(false);
            return new FakeRowsReader(totalRows);
        }
    }

    private sealed class FakeParamCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];
        public override int Count => _items.Count;
        public override object SyncRoot => this;
        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (var v in values) Add(v!); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains((DbParameter)value);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_items).CopyTo(array, index);
        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _items.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) { var i = IndexOf(parameterName); if (i >= 0) RemoveAt(i); }
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }

    private sealed class FakeDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;
        public override int Size { get; set; }
        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class FakeDbTransaction(DbConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => connection;
        public override void Commit() { }
        public override void Rollback() { }
    }

    private sealed class FakeRowsReader(int totalRows) : DbDataReader
    {
        private int _index = -1;
        public override int FieldCount => 2;
        public override bool HasRows => totalRows > 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => -1;
        public override int Depth => 0;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override bool GetBoolean(int ordinal) => false;
        public override byte GetByte(int ordinal) => 0;
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => '\0';
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override string GetDataTypeName(int ordinal) => ordinal == 0 ? "integer" : "varchar";
        public override DateTime GetDateTime(int ordinal) => DateTime.MinValue;
        public override decimal GetDecimal(int ordinal) => 0m;
        public override double GetDouble(int ordinal) => 0d;
        public override Type GetFieldType(int ordinal) => ordinal == 0 ? typeof(int) : typeof(string);
        public override float GetFloat(int ordinal) => 0f;
        public override Guid GetGuid(int ordinal) => Guid.Empty;
        public override short GetInt16(int ordinal) => 0;
        public override int GetInt32(int ordinal) => _index + 1;
        public override long GetInt64(int ordinal) => _index + 1L;
        public override string GetName(int ordinal) => ordinal == 0 ? "id" : "name";
        public override int GetOrdinal(string name) => string.Equals(name, "id", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        public override string GetString(int ordinal) => ordinal == 0 ? (_index + 1).ToString(CultureInfo.InvariantCulture) : $"row-{_index + 1}";
        public override object GetValue(int ordinal) => ordinal == 0 ? _index + 1 : $"row-{_index + 1}";
        public override int GetValues(object[] values) => 0;
        public override bool IsDBNull(int ordinal) => false;
        public override bool NextResult() => false;
        public override bool Read() => ++_index < totalRows;
        public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();
    }

    private sealed class StubFederatedQueryExecutionService : IFederatedQueryExecutionService
    {
        public static readonly StubFederatedQueryExecutionService Empty = new();
        public Task ExecuteGovernedQueryAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, Func<DbDataReader, CancellationToken, Task> rowWriter, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubMcpApiDispatcherService : IApiDispatcherService
    {
        public Task<string> DescribeApiAsync(string endpoint, string? method = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> InvokeApiAsync(string endpoint, string method, IReadOnlyDictionary<string, object?>? parameters = null, object? body = null, DomainRequestContext? context = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetOpenApiJsonAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetEndpointsDocumentationAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetMcpToolsDocumentationAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetQueryGuideDocumentationAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;
        public T Get(string? name) => currentValue;
        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;
        private sealed class NullDisposable : IDisposable { public static readonly NullDisposable Instance = new(); public void Dispose() { } }
    }

    private sealed class FixedExecutorProvider(IRequestExecutor executor) : IRequestExecutorProvider
    {
        public ImmutableArray<string> SchemaNames { get; } = [ISchemaDefinition.DefaultName];
        public ValueTask<IRequestExecutor> GetExecutorAsync(string? schemaName = null, CancellationToken cancellationToken = default) => ValueTask.FromResult(executor);
    }

    private sealed class StubGraphQlExecutor : IRequestExecutor
    {
        public ulong Version => 1;
        public ISchemaDefinition Schema => null!;
        public IFeatureCollection Features { get; } = new FeatureCollection();
        public Task<IExecutionResult> ExecuteAsync(IOperationRequest request, CancellationToken cancellationToken = default) => Task.FromResult<IExecutionResult>(OperationResult.FromError(ErrorBuilder.New().SetMessage("ok").Build()));
        public Task<IResponseStream> ExecuteBatchAsync(OperationRequestBatch requestBatch, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static async Task WaitCanceledAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = ct.Register(static s => ((TaskCompletionSource)s!).TrySetCanceled(), tcs);
        await tcs.Task;
    }

    private sealed class DelayingSqlExecutionService(TaskCompletionSource started) : IGovernedSqlExecutionService
    {
        public Task ExecuteGovernedQueryAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, Func<DbDataReader, CancellationToken, Task> rowWriter, CancellationToken ct = default) => throw new NotSupportedException();
        public async Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
        {
            started.TrySetResult();
            await WaitCanceledAsync(ct);
            return new GovernedSqlResult(request.Sql, [], [], 0, 0, false);
        }
        public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class DelayingGraphQlExecutor(TaskCompletionSource started) : IRequestExecutor
    {
        public ulong Version => 1;
        public ISchemaDefinition Schema => null!;
        public IFeatureCollection Features { get; } = new FeatureCollection();
        public async Task<IExecutionResult> ExecuteAsync(IOperationRequest request, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await WaitCanceledAsync(cancellationToken);
            return OperationResult.FromError(ErrorBuilder.New().SetMessage("ok").Build());
        }
        public Task<IResponseStream> ExecuteBatchAsync(OperationRequestBatch requestBatch, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
