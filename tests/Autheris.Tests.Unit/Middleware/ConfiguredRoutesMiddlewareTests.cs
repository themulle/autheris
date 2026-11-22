using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Runtime.ResourceGroups;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Middleware;

public sealed class ConfiguredRoutesMiddlewareTests
{
    private sealed class RecordingAuditRepository : IAuditLogRepository
    {
        public List<AuditLogEntry> Entries { get; } = [];
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
        public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class RecordingResourceGroupManager : IResourceGroupManager
    {
        private sealed class NoopDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public List<ResourceGroupTier> AcquiredTiers { get; } = [];
        public ValueTask<ResourceGroupLeaseResult> TryAcquireLeaseAsync(ResourceGroupTier tier, string tenantId, CancellationToken cancellationToken = default)
        {
            AcquiredTiers.Add(tier);
            return ValueTask.FromResult(ResourceGroupLeaseResult.Acquired(tier, new NoopDisposable()));
        }

        public ResourceGroupMetrics GetMetrics() => throw new NotSupportedException();
    }

    [Fact]
    public async Task ResourceGroupMiddleware_CustomMcpPath_ClassifiesAgentTierAndSseSlots()
    {
        var options = new GatewayOptions { Mcp = new McpOptions { EndpointPath = "/custom-mcp" } };
        var mgr = new RecordingResourceGroupManager();
        var mw = new ResourceGroupMiddleware(
            _ => Task.CompletedTask,
            mgr,
            Options.Create(options),
            NullLogger<ResourceGroupMiddleware>.Instance);

        var postCtx = new DefaultHttpContext();
        postCtx.Request.Method = HttpMethods.Post;
        postCtx.Request.Path = "/custom-mcp";
        postCtx.Request.Headers.Accept = "application/json, text/event-stream";
        await mw.InvokeAsync(postCtx);
        mgr.AcquiredTiers.ShouldBe([ResourceGroupTier.AutonomousAgents]);

        ResourceGroupMiddleware.IsPersistentConnectionRequest(postCtx, options).ShouldBeFalse();

        var sseCtx = new DefaultHttpContext();
        sseCtx.Request.Method = HttpMethods.Get;
        sseCtx.Request.Path = "/custom-mcp/sse";
        ResourceGroupMiddleware.IsPersistentConnectionRequest(sseCtx, options).ShouldBeTrue();

        var oldMcpCtx = new DefaultHttpContext();
        oldMcpCtx.Request.Method = HttpMethods.Post;
        oldMcpCtx.Request.Path = "/mcp";
        await mw.InvokeAsync(oldMcpCtx);
        mgr.AcquiredTiers.ShouldBe([ResourceGroupTier.AutonomousAgents, ResourceGroupTier.Interactive]);
    }

    [Fact]
    public async Task AccessAuditMiddleware_CustomMcpAndGraphQlPaths_ResolveConfiguredChannels()
    {
        var options = new GatewayOptions
        {
            Mcp = new McpOptions { EndpointPath = "/custom-mcp" },
            GraphQL = new GraphQLOptions { EndpointPath = "/custom-gql" }
        };
        var repo = new RecordingAuditRepository();
        var mw = new AccessAuditMiddleware(
            _ => Task.CompletedTask,
            Options.Create(options),
            NullLogger<AccessAuditMiddleware>.Instance);

        var mcpCtx = new DefaultHttpContext();
        mcpCtx.Request.Path = "/custom-mcp/tools/call";
        var mcpAudit = new AuditContext();
        await mw.InvokeAsync(mcpCtx, new AuditWriter(repo, NullLogger<AuditWriter>.Instance), mcpAudit);
        mcpAudit.Channel.ShouldBe("MCP");

        var oldMcpCtx = new DefaultHttpContext();
        oldMcpCtx.Request.Path = "/mcp";
        var oldMcpAudit = new AuditContext();
        await mw.InvokeAsync(oldMcpCtx, new AuditWriter(repo, NullLogger<AuditWriter>.Instance), oldMcpAudit);
        oldMcpAudit.Channel.ShouldBe("REST");

        var gqlCtx = new DefaultHttpContext();
        gqlCtx.Request.Path = "/custom-gql";
        var gqlAudit = new AuditContext();
        await mw.InvokeAsync(gqlCtx, new AuditWriter(repo, NullLogger<AuditWriter>.Instance), gqlAudit);
        gqlAudit.Channel.ShouldBe("GraphQL");
    }

    private sealed class DenyingRateLimiter : IRateLimiterService
    {
        public int PreAuthCalls { get; private set; }
        public int PostAuthCalls { get; private set; }

        public Task<RateLimitResult> CheckPreAuthIpAsync(string ip, PreAuthIpRateLimitOptions options, CancellationToken ct = default)
        {
            PreAuthCalls++;
            return Task.FromResult(new RateLimitResult(false, 60));
        }

        public Task<RateLimitResult> CheckPostAuthSidAsync(string sid, PostAuthSidRateLimitOptions options, CancellationToken ct = default)
        {
            PostAuthCalls++;
            return Task.FromResult(new RateLimitResult(false, 60));
        }

        public Task<CostQuotaResult> CheckCostQuotaAsync(string key, int requestedCost, ClientQuotaPolicy policy, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CostQuotaResult> ReserveCostQuotaAsync(string key, int reservedCost, ClientQuotaPolicy policy, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CostQuotaResult> SettleCostQuotaAsync(string key, int reservedCost, int actualCost, ClientQuotaPolicy policy, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CallerBucketState?> GetBucketStateAsync(string key, ClientQuotaPolicy policy, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CallerBucketState?> GetBucketStateAsync(string key, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static Endpoint EndpointWith(params object[] metadata) =>
        new(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test-endpoint");

    private static ClaimsPrincipal ReadOnlyPrincipal()
    {
        var identity = new ClaimsIdentity([new Claim("oid", "33333333-3333-3333-3333-333333333333")], "Bearer");
        TokenAccessScope.MarkReadOnly(identity);
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task ResourceGroupMiddleware_HonorsGatewayEndpointMetadata_OnNonDefaultPaths()
    {
        var mgr = new RecordingResourceGroupManager();
        var mw = new ResourceGroupMiddleware(
            _ => Task.CompletedTask,
            mgr,
            Options.Create(new GatewayOptions()),
            NullLogger<ResourceGroupMiddleware>.Instance);

        var healthCtx = new DefaultHttpContext();
        healthCtx.Request.Path = "/probe/live";
        healthCtx.SetEndpoint(EndpointWith(HealthProbeMetadata.Instance));
        GatewayEndpointMetadata.IsHealthProbe(healthCtx).ShouldBeTrue();
        await mw.InvokeAsync(healthCtx);

        var metricsCtx = new DefaultHttpContext();
        metricsCtx.Request.Path = "/telemetry/prom";
        metricsCtx.SetEndpoint(EndpointWith(MetricsEndpointMetadata.Instance));
        GatewayEndpointMetadata.IsMetricsEndpoint(metricsCtx).ShouldBeTrue();
        await mw.InvokeAsync(metricsCtx);

        mgr.AcquiredTiers.ShouldBeEmpty();

        var mcpCtx = new DefaultHttpContext();
        mcpCtx.Request.Path = "/alt-mcp";
        mcpCtx.SetEndpoint(EndpointWith(new IngressTransportMetadata(DataSourceTransportFlags.Mcp)));
        await mw.InvokeAsync(mcpCtx);

        var odataCtx = new DefaultHttpContext();
        odataCtx.Request.Path = "/alt-feed/Orders";
        odataCtx.SetEndpoint(EndpointWith(new IngressTransportMetadata(DataSourceTransportFlags.OData)));
        await mw.InvokeAsync(odataCtx);

        mgr.AcquiredTiers.ShouldBe([ResourceGroupTier.AutonomousAgents, ResourceGroupTier.BulkAnalytics]);
    }

    [Fact]
    public async Task RateLimitAndHostAllowListMiddleware_HonorHealthAndMetricsMetadata()
    {
        var limiter = new DenyingRateLimiter();
        var opts = Options.Create(new GatewayOptions());

        var preNext = false;
        var preMw = new PreAuthIpRateLimitingMiddleware(_ => { preNext = true; return Task.CompletedTask; }, opts, limiter);
        var healthCtx = new DefaultHttpContext();
        healthCtx.Request.Path = "/custom-live";
        healthCtx.SetEndpoint(EndpointWith(HealthProbeMetadata.Instance));
        await preMw.InvokeAsync(healthCtx);
        preNext.ShouldBeTrue();
        limiter.PreAuthCalls.ShouldBe(0);

        var postNext = false;
        var postMw = new PostAuthSidRateLimitingMiddleware(_ => { postNext = true; return Task.CompletedTask; }, opts, limiter);
        var metricsCtx = new DefaultHttpContext { User = ReadOnlyPrincipal() };
        metricsCtx.Request.Path = "/custom-metrics";
        metricsCtx.SetEndpoint(EndpointWith(MetricsEndpointMetadata.Instance));
        await postMw.InvokeAsync(metricsCtx);
        postNext.ShouldBeTrue();
        limiter.PostAuthCalls.ShouldBe(0);

        var hostNext = false;
        var hostMw = new HostAllowListMiddleware(_ => { hostNext = true; return Task.CompletedTask; }, new HostAllowList(["gateway.corp.example"]));
        var podProbeCtx = new DefaultHttpContext();
        podProbeCtx.Request.Host = new HostString("10.0.0.5");
        podProbeCtx.Request.Path = "/custom-ready";
        podProbeCtx.SetEndpoint(EndpointWith(HealthProbeMetadata.Instance));
        await hostMw.InvokeAsync(podProbeCtx);
        hostNext.ShouldBeTrue();
    }

    [Fact]
    public async Task GatewayCorsCsrfAndSecurityHeaders_HonorEndpointMetadata()
    {
        var options = new GatewayOptions
        {
            Hosting = new HostingLimitsOptions { AllowedHosts = ["gateway.corp.example"] },
            OData = new ODataOptions { Enabled = true }
        };
        var allowList = new HostAllowList(options.Hosting.AllowedHosts);
        var env = new TestHostEnvironment("Production");

        var csrfPassed = false;
        var csrfMw = new GatewayCorsCsrfMiddleware(_ => { csrfPassed = true; return Task.CompletedTask; }, options, allowList, env);

        var loginCtx = new DefaultHttpContext();
        loginCtx.Request.Method = HttpMethods.Post;
        loginCtx.Request.Scheme = "https";
        loginCtx.Request.Path = "/custom-auth/exchange";
        loginCtx.Request.Headers.Origin = "https://gateway.corp.example";
        loginCtx.Request.Headers.Cookie = "session=1";
        loginCtx.SetEndpoint(EndpointWith(CsrfLoginExemptMetadata.Instance));
        GatewayEndpointMetadata.IsCsrfLoginExempt(loginCtx).ShouldBeTrue();
        await csrfMw.InvokeAsync(loginCtx);
        csrfPassed.ShouldBeTrue();

        var blockedCtx = new DefaultHttpContext();
        blockedCtx.Request.Method = HttpMethods.Post;
        blockedCtx.Request.Scheme = "https";
        blockedCtx.Request.Path = "/custom-auth/other";
        blockedCtx.Request.Headers.Origin = "https://gateway.corp.example";
        blockedCtx.Request.Headers.Cookie = "session=1";
        blockedCtx.Response.Body = new MemoryStream();
        await csrfMw.InvokeAsync(blockedCtx);
        blockedCtx.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        var secMw = new SecurityHeadersMiddleware(_ => Task.CompletedTask, options, env);

        var docCtx = new DefaultHttpContext();
        docCtx.Request.Path = "/custom-docs";
        docCtx.SetEndpoint(EndpointWith(PublicDocumentMetadata.Instance));
        GatewayEndpointMetadata.IsPublicDocument(docCtx).ShouldBeTrue();
        await secMw.InvokeAsync(docCtx);
        docCtx.Response.Headers.CacheControl.ToString().ShouldBeEmpty();

        var privateCtx = new DefaultHttpContext();
        privateCtx.Request.Path = "/custom-docs";
        await secMw.InvokeAsync(privateCtx);
        privateCtx.Response.Headers.CacheControl.ToString().ShouldBe("no-store");

        var odataCtx = new DefaultHttpContext();
        odataCtx.Request.Method = HttpMethods.Get;
        odataCtx.Request.Path = "/custom-odata/Orders";
        odataCtx.SetEndpoint(EndpointWith(new IngressTransportMetadata(DataSourceTransportFlags.OData)));
        await secMw.InvokeAsync(odataCtx);
        odataCtx.Response.Headers["OData-Version"].ToString().ShouldBe("4.0");
    }

    [Theory]
    [InlineData("POST", "/alt-gql", false, true)]
    [InlineData("PUT", "/alt-gql", false, false)]
    [InlineData("POST", "/alt-proc/run_report", true, true)]
    [InlineData("POST", "/alt-proc/openapi.json", true, false)]
    [InlineData("POST", "/alt-proc/openapi.json/", true, false)]
    [InlineData("POST", "/alt-proc/run_report/", true, false)]
    [InlineData("POST", "/alt-proc/..", true, false)]
    [InlineData("POST", "/alt-proc/.", true, false)]
    [InlineData("POST", "/alt-proc/a%2Fb", true, false)]
    [InlineData("DELETE", "/alt-sql/statements/stmt-1", true, true)]
    [InlineData("DELETE", "/alt-sql/statements/.", true, false)]
    public async Task ReadOnlyTokenMiddleware_HonorsReadOnlyTokenAccessMetadata(
        string method, string path, bool strictRawPath, bool expectedAllowed)
    {
        var passed = false;
        var mw = new ReadOnlyTokenMiddleware(_ => { passed = true; return Task.CompletedTask; }, "/graphql", "/mcp");
        var ctx = new DefaultHttpContext { User = ReadOnlyPrincipal() };
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        var allowedMethod = method == HttpMethods.Delete ? HttpMethods.Delete : HttpMethods.Post;
        ctx.SetEndpoint(EndpointWith(new ReadOnlyTokenAccessMetadata(allowedMethod, strictRawPath)));

        await mw.InvokeAsync(ctx);

        passed.ShouldBe(expectedAllowed);
        if (!expectedAllowed)
        {
            ctx.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        }
    }
}
