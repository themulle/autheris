namespace Autheris.Tests.Unit.Jobs;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Runtime.Jobs.Interfaces;
using Autheris.Application.Runtime.Jobs.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class AsyncJobKillSwitchTests
{
    private sealed class RouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
    {
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IServiceProvider ServiceProvider => services;
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(services);
    }

    private sealed class NoopJobManager : IAsyncQueryJobManager
    {
        public Task<AsyncJobDescriptor> SubmitJobAsync(AsyncQueryJobRequest request, ClaimsPrincipal caller, TenantId tenantId, DataSourceTransportFlags ingress = DataSourceTransportFlags.None, CancellationToken ct = default) =>
            throw new InvalidOperationException("Submit should not be invoked when disabled.");

        public Task<AsyncJobDescriptor?> GetJobAsync(string jobId, TenantId tenantId, string callerUserSid, CancellationToken ct = default) =>
            throw new InvalidOperationException("GetJob should not be invoked when disabled.");

        public Task<Stream?> GetJobResultStreamAsync(string jobId, TenantId tenantId, string callerUserSid, CancellationToken ct = default) =>
            throw new InvalidOperationException("GetResult should not be invoked when disabled.");

        public Task<bool> CancelJobAsync(string jobId, TenantId tenantId, string callerUserSid, CancellationToken ct = default) =>
            throw new InvalidOperationException("Cancel should not be invoked when disabled.");

        public Task PurgeExpiredJobsAsync(TimeSpan retention, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingQueue(string scratchDirectory) : IAsyncJobQueue
    {
        private readonly Channel<AsyncJobExecutionItem> _channel = Channel.CreateUnbounded<AsyncJobExecutionItem>();
        public List<AsyncJobDescriptor> Transitions { get; } = [];

        public ChannelReader<AsyncJobExecutionItem> Reader => _channel.Reader;
        public string ScratchDirectory => scratchDirectory;

        public void Enqueue(AsyncJobExecutionItem item) => _channel.Writer.TryWrite(item).ShouldBeTrue();

        public ValueTask<bool> TryTransitionAsync(AsyncJobDescriptor next, CancellationToken ct)
        {
            Transitions.Add(next);
            return ValueTask.FromResult(true);
        }

        public ValueTask SweepOrphanedScratchFilesAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ApplyCancelSignalAsync(AsyncJobCancelSignal signal, CancellationToken ct) => ValueTask.CompletedTask;
        public void Complete() => _channel.Writer.TryComplete();
    }

    private sealed class MutableMonitor<T>(T initial) : IOptionsMonitor<T>
    {
        private T _current = initial;
        public T CurrentValue => _current;
        public T Get(string? name) => _current;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
        public void Set(T value) => _current = value;
    }

    private sealed class TrackingSqlExecutionService : IGovernedSqlExecutionService
    {
        public int CallCount { get; private set; }
        public Task ExecuteGovernedQueryAsync(GovernedSqlQueryRequest request, ClaimsPrincipal caller, TenantId tenantId, Func<DbDataReader, CancellationToken, Task> onResultAsync, CancellationToken ct = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }

        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest request, ClaimsPrincipal caller, TenantId tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ValidateAsync(string sql, ClaimsPrincipal caller, TenantId tenantId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubDisabledAsyncJobsSwitches : IReadPathSwitches
    {
        public bool RestDataEndpointEnabled => true;
        public bool BulkStreamEnabled => true;
        public bool QueryAnalysisEnabled => true;
        public bool ProceduresEnabled => false;
        public bool AsyncJobsEnabled => false;
        public bool IsGroupEnabled(McpToolGroup group) => true;
        public bool IsToolEnabled(string toolName) => false;
        public string? DisabledReason(string toolName) => "Disabled";
        public bool IsResourceEnabled(string resourceUri) => true;
        public bool IsPromptEnabled(string promptName) => true;
        public void RecordDropped(string toolName) { }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Disabled_switch_returns_404_for_job_routes()
    {
        ReadPathSwitches.From(new GatewayOptions()).AsyncJobsEnabled.ShouldBeTrue();

        var offSwitches = new StubDisabledAsyncJobsSwitches();
        offSwitches.AsyncJobsEnabled.ShouldBeFalse();

        // 1. Startup gate prevents mapping job endpoints at all
        var startupServices = new ServiceCollection();
        startupServices.AddLogging();
        startupServices.AddSingleton<IAsyncQueryJobManager>(new NoopJobManager());
        startupServices.AddSingleton<IReadPathSwitches>(offSwitches);
        var startupRoutes = new RouteBuilder(startupServices.BuildServiceProvider());
        if (offSwitches.AsyncJobsEnabled)
        {
            startupRoutes.MapAsyncJobEndpoints();
        }
        startupRoutes.DataSources.SelectMany(d => d.Endpoints).ShouldBeEmpty();

        // 2. Even when job routes are mapped, disabled IReadPathSwitches returns 404 for every job route
        var liveServices = new ServiceCollection();
        liveServices.AddLogging();
        liveServices.AddSingleton<IAsyncQueryJobManager>(new NoopJobManager());
        liveServices.AddSingleton<IReadPathSwitches>(offSwitches);
        var provider = liveServices.BuildServiceProvider();

        var routes = new RouteBuilder(provider);
        routes.MapAsyncJobEndpoints();
        var endpoints = routes.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
        endpoints.Count.ShouldBe(4);

        foreach (var endpoint in endpoints)
        {
            var ctx = new DefaultHttpContext { RequestServices = provider };
            ctx.Response.Body = new MemoryStream();
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u1"), new Claim("sid", "S-1-u1"), new Claim("tenant", "default")], "Test"));
            ctx.Request.RouteValues["jobId"] = "job-1";
            if (endpoint.RoutePattern.RawText == "/api/v1/jobs/query")
            {
                ctx.Request.Method = HttpMethods.Post;
                ctx.Request.ContentType = "application/json";
                ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"query\":\"SELECT 1\"}"));
            }

            await endpoint.RequestDelegate!(ctx);
            ctx.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound, endpoint.RoutePattern.RawText);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Disabled_switch_stops_worker_and_hides_mcp_tool()
    {
        var scratchDir = Path.Combine(Path.GetTempPath(), $"autheris-killswitch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchDir);
        try
        {
            var disabledJobOpts = Options.Create(new AsyncJobOptions { ScratchDirectory = scratchDir });
            var gatewayOpts = Options.Create(new GatewayOptions { AsyncJobs = disabledJobOpts.Value });
            var trackingSql = new TrackingSqlExecutionService();

            var services = new ServiceCollection();
            services.AddScoped<IGovernedSqlExecutionService>(_ => trackingSql);
            await using var provider = services.BuildServiceProvider();

            var queue = new RecordingQueue(scratchDir);
            var preQueuedDescriptor = new AsyncJobDescriptor(
                "prequeued-1", "default", "S-1-u1", "SELECT 1", "json", AsyncJobState.Queued, DateTimeOffset.UtcNow);
            var itemCts = new CancellationTokenSource();
            var queuedItem = new AsyncJobExecutionItem(
                preQueuedDescriptor,
                new AsyncQueryJobRequest("SELECT 1"),
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u1"), new Claim("sid", "S-1-u1")], "Test")),
                itemCts,
                DataSourceTransportFlags.WebSql);
            queue.Enqueue(queuedItem);

            var switchesOff = new StubDisabledAsyncJobsSwitches();
            var worker = new AsyncQueryJobBackgroundWorker(
                new NoopJobManager(),
                queue,
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullAuditLogRepository.Instance,
                NullLogger<AsyncQueryJobBackgroundWorker>.Instance,
                gatewayOpts,
                disabledJobOpts,
                switches: switchesOff);

            await worker.StartAsync(CancellationToken.None);
            if (worker.ExecuteTask is { } task)
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            await worker.StopAsync(CancellationToken.None);

            // Worker dequeued nothing, executed nothing, and left queued item inert
            trackingSql.CallCount.ShouldBe(0);
            queue.Transitions.ShouldBeEmpty();
            queue.Reader.Count.ShouldBe(1);

            // MCP async job tool gating via ReadPathSwitches & McpToolRegistry
            switchesOff.IsToolEnabled("submit_async_query").ShouldBeFalse();
            switchesOff.IsToolEnabled("async_query").ShouldBeFalse();
            switchesOff.IsToolEnabled("get_async_job_status").ShouldBeFalse();

            var registry = new McpToolRegistry(gatewayOpts, switches: switchesOff);
            registry.RegisterTool(new McpToolDefinition("submit_async_query", "Submit an async SQL job", "{}", "submitAsyncQuery"));
            registry.GetAvailableTools().ShouldNotContain(t => t.Name == "submit_async_query");
            registry.FindTool("submit_async_query").ShouldBeNull();
        }
        finally
        {
            try { Directory.Delete(scratchDir, true); } catch { }
        }
    }
}
