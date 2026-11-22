using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Runtime.ResourceGroups;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
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
}
