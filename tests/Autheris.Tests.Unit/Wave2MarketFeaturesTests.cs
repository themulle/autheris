namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Observability;
using Autheris.Application.ResourceGroups;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

public sealed class Wave2MarketFeaturesTests
{
    // =========================================================================
    // 1. F-PERF-08: Hierarchical Resource Groups & Workload Queuing Tests
    // =========================================================================

    [Fact]
    public async Task ResourceGroupManager_FastPath_AcquiresAndReleasesLease()
    {
        var options = Options.Create(new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions
            {
                Enabled = true,
                Interactive = new ResourceGroupTierConfigOptions(MaxConcurrency: 2, MaxQueueDepth: 5, TimeoutSeconds: 1)
            }
        });

        using var manager = new ResourceGroupManager(options, NullLogger<ResourceGroupManager>.Instance);

        var result = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "tenant-a");
        Assert.True(result.Success);
        Assert.NotNull(result.Lease);
        Assert.Null(result.RejectionReason);

        var metrics = manager.GetMetrics();
        var interactiveMetrics = Assert.Single(metrics.Tiers, t => t.Tier == ResourceGroupTier.Interactive);
        Assert.Equal(1, interactiveMetrics.ActiveConcurrency);
        Assert.Equal(1, interactiveMetrics.TotalAcquired);

        // Dispose lease
        await result.Lease!.DisposeAsync();

        metrics = manager.GetMetrics();
        interactiveMetrics = Assert.Single(metrics.Tiers, t => t.Tier == ResourceGroupTier.Interactive);
        Assert.Equal(0, interactiveMetrics.ActiveConcurrency);
    }

    [Fact]
    public async Task ResourceGroupManager_QueueFull_RejectsImmediately()
    {
        var options = Options.Create(new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions
            {
                Enabled = true,
                AutonomousAgents = new ResourceGroupTierConfigOptions(MaxConcurrency: 1, MaxQueueDepth: 1, TimeoutSeconds: 2)
            }
        });

        using var manager = new ResourceGroupManager(options, NullLogger<ResourceGroupManager>.Instance);

        // 1. Take slot
        var lease1 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.AutonomousAgents, "tenant-1");
        Assert.True(lease1.Success);

        // 2. Put 1 in queue (MaxQueueDepth is 1)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var queuedTask = Task.Run(async () => await manager.TryAcquireLeaseAsync(ResourceGroupTier.AutonomousAgents, "tenant-2", cts.Token));

        // Allow task to hit the queue
        await Task.Delay(100);

        // 3. Next request exceeds queue depth -> immediate QueueFull rejection
        var rejectedResult = await manager.TryAcquireLeaseAsync(ResourceGroupTier.AutonomousAgents, "tenant-3");
        Assert.False(rejectedResult.Success);
        Assert.Equal("QueueFull", rejectedResult.RejectionReason);

        var metrics = manager.GetMetrics();
        var agentMetrics = Assert.Single(metrics.Tiers, t => t.Tier == ResourceGroupTier.AutonomousAgents);
        Assert.True(agentMetrics.TotalRejectedQueueFull >= 1);

        // Cleanup
        await lease1.Lease!.DisposeAsync();
        try { await queuedTask; } catch { }
    }

    [Fact]
    public async Task ResourceGroupManager_Timeout_RejectsWhenSlotNotReleasedInTime()
    {
        var options = Options.Create(new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions
            {
                Enabled = true,
                BulkAnalytics = new ResourceGroupTierConfigOptions(MaxConcurrency: 1, MaxQueueDepth: 5, TimeoutSeconds: 1)
            }
        });

        using var manager = new ResourceGroupManager(options, NullLogger<ResourceGroupManager>.Instance);

        // Occupy the only slot
        var lease1 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.BulkAnalytics, "tenant-bulk");
        Assert.True(lease1.Success);

        // Attempt second lease with short timeout (1s)
        var timedOut = await manager.TryAcquireLeaseAsync(ResourceGroupTier.BulkAnalytics, "tenant-bulk-2");
        Assert.False(timedOut.Success);
        Assert.Equal("Timeout", timedOut.RejectionReason);

        var metrics = manager.GetMetrics();
        var bulkMetrics = Assert.Single(metrics.Tiers, t => t.Tier == ResourceGroupTier.BulkAnalytics);
        Assert.True(bulkMetrics.TotalRejectedTimeout >= 1);

        await lease1.Lease!.DisposeAsync();
    }

    [Theory]
    [InlineData("/graphql", null, ResourceGroupTier.Interactive)]
    [InlineData("/mcp/v1", null, ResourceGroupTier.AutonomousAgents)]
    [InlineData("/odata/v4", null, ResourceGroupTier.BulkAnalytics)]
    [InlineData("/api/query", "AutonomousAgent", ResourceGroupTier.AutonomousAgents)]
    [InlineData("/api/query", "BulkAnalytics", ResourceGroupTier.BulkAnalytics)]
    public async Task ResourceGroupMiddleware_ClassifiesWorkloadAndSetsHeader(string requestPath, string? headerTier, ResourceGroupTier expectedTier)
    {
        var options = Options.Create(new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions { Enabled = true }
        });
        using var manager = new ResourceGroupManager(options, NullLogger<ResourceGroupManager>.Instance);

        bool nextInvoked = false;
        RequestDelegate next = ctx =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        };

        var middleware = new ResourceGroupMiddleware(next, manager, options, NullLogger<ResourceGroupMiddleware>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = requestPath;
        if (headerTier != null)
        {
            httpContext.Request.Headers["X-Workload-Tier"] = headerTier;
        }

        await middleware.InvokeAsync(httpContext);

        Assert.True(nextInvoked);
        Assert.Equal(expectedTier.ToString(), httpContext.Response.Headers["X-Resource-Group-Tier"].ToString());
    }

    [Fact]
    public async Task ResourceGroupMiddleware_Returns429_WhenQueueFull()
    {
        var options = Options.Create(new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions
            {
                Enabled = true,
                Interactive = new ResourceGroupTierConfigOptions(MaxConcurrency: 1, MaxQueueDepth: 0, TimeoutSeconds: 1)
            }
        });
        using var manager = new ResourceGroupManager(options, NullLogger<ResourceGroupManager>.Instance);

        // Saturate concurrency
        var lease = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "t1");
        Assert.True(lease.Success);

        var middleware = new ResourceGroupMiddleware(
            ctx => Task.CompletedTask,
            manager,
            options,
            NullLogger<ResourceGroupMiddleware>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/graphql";
        httpContext.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(httpContext);

        Assert.Equal(StatusCodes.Status429TooManyRequests, httpContext.Response.StatusCode);
        Assert.Equal("5", httpContext.Response.Headers["Retry-After"].ToString());
        Assert.Equal("Interactive", httpContext.Response.Headers["X-Resource-Group-Tier"].ToString());

        await lease.Lease!.DisposeAsync();
    }

    // =========================================================================
    // 2. F-API-07: Canonical System Metadata & Monitoring Schema Tests
    // =========================================================================

    [Fact]
    public async Task GatewaySystemMetricsService_CollectsComprehensiveMetrics()
    {
        var options = Options.Create(new GatewayOptions());
        using var manager = new ResourceGroupManager(options, NullLogger<ResourceGroupManager>.Instance);

        var service = new GatewaySystemMetricsService(
            manager,
            NullLogger<GatewaySystemMetricsService>.Instance,
            dbtCircuitBreaker: null,
            healthCheckService: null);

        var metrics = await service.CollectSystemMetricsAsync();

        Assert.NotNull(metrics);
        Assert.False(string.IsNullOrWhiteSpace(metrics.Version));
        Assert.True(metrics.Uptime >= TimeSpan.Zero);
        Assert.True(metrics.MemoryAllocatedBytes > 0);
        Assert.True(metrics.ThreadCount > 0);
        Assert.NotNull(metrics.ResourceGroups);
        Assert.NotEmpty(metrics.ResourceGroups.Tiers);
        Assert.NotEmpty(metrics.Components);

        var coreHealth = Assert.Single(metrics.Components, c => c.ComponentName == "CoreGateway");
        Assert.Equal("Healthy", coreHealth.Status);
    }

    // =========================================================================
    // 3. F-AI-03: Dynamic Few-Shot Golden Query Injection Tests
    // =========================================================================

    [Fact]
    public async Task GoldenQueryService_InitializesDefaultsAndFiltersByDomainAndTable()
    {
        var options = Options.Create(new GatewayOptions());
        var service = new GoldenQueryService(options, NullLogger<GoldenQueryService>.Instance);

        var allQueries = await service.GetGoldenQueriesAsync();
        Assert.True(allQueries.Count >= 2);

        var customerQueries = await service.GetGoldenQueriesAsync(domain: "finance", tableName: "customers");
        Assert.NotEmpty(customerQueries);
        Assert.All(customerQueries, q =>
        {
            Assert.Equal("finance", q.Domain);
            Assert.Equal("customers", q.TableName);
            Assert.Contains("customers", q.QueryText);
        });

        // Add custom Golden Query
        service.RegisterGoldenQuery(new GoldenQuery(
            Id: "custom_query_1",
            Domain: "crm",
            TableName: "leads",
            Title: "Get Top Leads",
            Description: "Returns hot leads",
            QueryText: "query { leads(status: \"HOT\") { id score } }"
        ));

        var crmQueries = await service.GetGoldenQueriesAsync(domain: "crm", tableName: "leads");
        var leadQuery = Assert.Single(crmQueries);
        Assert.Equal("custom_query_1", leadQuery.Id);
    }

    [Fact]
    public async Task SemanticMcpCompiler_GeneratesExamplesResourceForGoldenQueries()
    {
        var goldenService = new GoldenQueryService(Options.Create(new GatewayOptions()), NullLogger<GoldenQueryService>.Instance);

        var repo = NSubstitute.Substitute.For<ITableMetadataRepository>();
        var sampleTable = new TableMetadata
        {
            Identifier = new TableIdentifier("finance", "dbo", "customers"),
            Table = new Table { SchemaName = "dbo", TableName = "customers", DisplayName = "Customers", Description = "Financial customer accounts" },
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "varchar" },
                new TableColumn { ColumnName = "iban", DataType = "varchar", IsSensitive = true }
            ]
        };

        repo.GetAllTablesAsync(NSubstitute.Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([sampleTable]));

        var compiler = new SemanticMcpCompiler(repo, NullLogger<SemanticMcpCompiler>.Instance, goldenService);

        var resources = await compiler.GetSemanticResourcesAsync("finance");
        var exampleResource = Assert.Single(resources, r => r.Uri == "examples://finance/customers");

        Assert.Equal("finance_customers_golden_queries", exampleResource.Name);
        Assert.Contains("Golden Queries & Verified Few-Shot Examples", exampleResource.Text);
        Assert.Contains("query GetActiveCustomers", exampleResource.Text);
    }

    [Fact]
    public async Task AiDataGuardrailService_ExecutesGetGoldenQueriesTool()
    {
        var toolRegistry = new McpToolRegistry();
        var goldenService = new GoldenQueryService(Options.Create(new GatewayOptions()), NullLogger<GoldenQueryService>.Instance);
        var guardrail = new AiDataGuardrailService(
            toolRegistry,
            Options.Create(new GatewayOptions()),
            NullLogger<AiDataGuardrailService>.Instance,
            goldenQueryService: goldenService);

        var sessionContext = new McpSessionContext("sess-1", "agent-1", "tenant-alpha", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var request = new McpToolCallRequest(
            ToolName: "get_golden_queries",
            ArgumentsJson: """{ "domain": "finance", "tableName": "customers" }""",
            SessionId: sessionContext.SessionId);

        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, sessionContext);

        Assert.True(result.IsSuccess);
        Assert.Contains("golden_customers_active", result.ContentJson);
        Assert.Contains("GetActiveCustomers", result.ContentJson);
    }

    // =========================================================================
    // 4. Security Findings & Quality Remediation Verification Tests
    // =========================================================================

    [Fact]
    public async Task ResourceGroupMiddleware_AntiNoisyNeighbor_PreventsMcpFromPromotingToInteractive_WhenUnprivileged()
    {
        var options = Options.Create(new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions { Enabled = true }
        });
        using var manager = new ResourceGroupManager(options, NullLogger<ResourceGroupManager>.Instance);

        var middleware = new ResourceGroupMiddleware(
            ctx => Task.CompletedTask,
            manager,
            options,
            NullLogger<ResourceGroupMiddleware>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/mcp/tools";
        httpContext.Request.Headers["X-Workload-Tier"] = "Interactive";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "StandardUser")], "TestAuth"));

        await middleware.InvokeAsync(httpContext);

        // Security check: Must NOT be promoted to Interactive!
        Assert.Equal("AutonomousAgents", httpContext.Response.Headers["X-Resource-Group-Tier"].ToString());
    }

    [Fact]
    public async Task ResourceGroupMiddleware_AntiNoisyNeighbor_AllowsClusterAdminToPromote()
    {
        var options = Options.Create(new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions { Enabled = true }
        });
        using var manager = new ResourceGroupManager(options, NullLogger<ResourceGroupManager>.Instance);

        var middleware = new ResourceGroupMiddleware(
            ctx => Task.CompletedTask,
            manager,
            options,
            NullLogger<ResourceGroupMiddleware>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/mcp/tools";
        httpContext.Request.Headers["X-Workload-Tier"] = "Interactive";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "ClusterAdmin")], "TestAuth"));

        await middleware.InvokeAsync(httpContext);

        Assert.Equal("Interactive", httpContext.Response.Headers["X-Resource-Group-Tier"].ToString());
    }

    [Fact]
    public async Task ResourceGroupMiddleware_AntiNoisyNeighbor_PreventsODataFromPromotingToInteractive()
    {
        var options = Options.Create(new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions { Enabled = true }
        });
        using var manager = new ResourceGroupManager(options, NullLogger<ResourceGroupManager>.Instance);

        var middleware = new ResourceGroupMiddleware(
            ctx => Task.CompletedTask,
            manager,
            options,
            NullLogger<ResourceGroupMiddleware>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/odata/v4/Invoices";
        httpContext.Request.Headers["X-Workload-Tier"] = "Interactive";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        await middleware.InvokeAsync(httpContext);

        Assert.Equal("BulkAnalytics", httpContext.Response.Headers["X-Resource-Group-Tier"].ToString());
    }

    [Theory]
    [InlineData("", "domain", "table", "query")]
    [InlineData("id", "", "table", "query")]
    [InlineData("id", "domain", "", "query")]
    [InlineData("id", "domain", "table", "")]
    public void GoldenQueryService_RegisterGoldenQuery_ThrowsOnMissingFields(string id, string domain, string table, string queryText)
    {
        var service = new GoldenQueryService(Options.Create(new GatewayOptions()), NullLogger<GoldenQueryService>.Instance);
        Assert.ThrowsAny<ArgumentException>(() =>
            service.RegisterGoldenQuery(new GoldenQuery(id, domain, table, "Title", "Desc", queryText)));
    }

    [Fact]
    public void GoldenQueryService_RegisterGoldenQuery_ThrowsWhenQueryExceedsMaxSize()
    {
        var service = new GoldenQueryService(Options.Create(new GatewayOptions()), NullLogger<GoldenQueryService>.Instance);
        var hugeQuery = new string('A', 70000);
        Assert.Throws<ArgumentException>(() =>
            service.RegisterGoldenQuery(new GoldenQuery("q-huge", "domain", "table", "Title", "Desc", hugeQuery)));
    }

    [Theory]
    [InlineData(0, 10, 5)]
    [InlineData(-1, 10, 5)]
    [InlineData(5, -1, 5)]
    [InlineData(5, 10, 0)]
    public void ResourceGroupTierConfig_ThrowsOnInvalidParameters(int maxConcurrency, int maxQueueDepth, int timeoutSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ResourceGroupTierConfig(ResourceGroupTier.Interactive, maxConcurrency, maxQueueDepth, TimeSpan.FromSeconds(timeoutSeconds)));
    }

    [Fact]
    public async Task ResourceGroupManager_AntiBarging_QueuesBehindExistingWaitingRequests()
    {
        var options = Options.Create(new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions
            {
                Enabled = true,
                Interactive = new ResourceGroupTierConfigOptions(MaxConcurrency: 1, MaxQueueDepth: 10, TimeoutSeconds: 2)
            }
        });
        using var manager = new ResourceGroupManager(options, NullLogger<ResourceGroupManager>.Instance);

        // 1. Acquire the only slot
        var lease1 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "t1");
        Assert.True(lease1.Success);

        // 2. Put request 2 into queue
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var queued2Task = Task.Run(async () => await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "t2", cts.Token));
        await Task.Delay(50); // Ensure queued

        var metrics = manager.GetMetrics();
        var tierMetrics = Assert.Single(metrics.Tiers, t => t.Tier == ResourceGroupTier.Interactive);
        Assert.Equal(1, tierMetrics.QueuedRequests);

        // 3. Release lease 1: Request 2 must acquire it seamlessly without any new request barging ahead
        await lease1.Lease!.DisposeAsync();

        var lease2 = await queued2Task;
        Assert.True(lease2.Success);

        await lease2.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task GatewaySystemMetricsService_ResourceGroupsHealth_ReportsDegradedWhenSaturated()
    {
        var options = Options.Create(new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions
            {
                Enabled = true,
                Interactive = new ResourceGroupTierConfigOptions(MaxConcurrency: 1, MaxQueueDepth: 1, TimeoutSeconds: 1)
            }
        });
        using var manager = new ResourceGroupManager(options, NullLogger<ResourceGroupManager>.Instance);

        // Fill slot and queue
        var lease1 = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "t1");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var queuedTask = Task.Run(async () => await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "t2", cts.Token));
        await Task.Delay(200);

        var service = new GatewaySystemMetricsService(
            manager,
            NullLogger<GatewaySystemMetricsService>.Instance);

        var metrics = await service.CollectSystemMetricsAsync();
        var rgHealth = Assert.Single(metrics.Components, c => c.ComponentName == "ResourceGroups");
        Assert.Equal("Degraded", rgHealth.Status);

        await lease1.Lease!.DisposeAsync();
        try { await queuedTask; } catch { }
    }
}
