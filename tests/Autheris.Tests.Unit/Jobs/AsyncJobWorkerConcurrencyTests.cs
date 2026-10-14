namespace Autheris.Tests.Unit.Jobs;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Jobs.Interfaces;
using Autheris.Application.Jobs.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-ARC-A-12: worker/queue contract, parallel scheduling, sticky terminal states and sweeper.</summary>
public sealed class AsyncJobWorkerConcurrencyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autheris-a12-" + Guid.NewGuid().ToString("N"));
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IHostEnvironment _env = Substitute.For<IHostEnvironment>();

    public AsyncJobWorkerConcurrencyTests()
    {
        Directory.CreateDirectory(_dir);
        _env.ContentRootPath.Returns(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    internal static ClaimsPrincipal User(string sid = "alice", string tenant = "t") => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, sid), new Claim("sub", sid), new Claim("tenant_id", tenant)], "Test"));

    internal static IServiceScopeFactory Scopes(IGovernedSqlExecutionService sql)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => sql);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    internal static GovernedSqlResult EmptyResult() =>
        new("s", ["id"], new List<IReadOnlyDictionary<string, object?>>(), 0, 1);

    /// <summary>A queue that is NOT the concrete manager: the worker must not depend on the downcast.</summary>
    private sealed class FakeQueue : IAsyncJobQueue
    {
        private readonly Channel<AsyncJobExecutionItem> _channel = Channel.CreateUnbounded<AsyncJobExecutionItem>();
        public List<AsyncJobDescriptor> Transitions { get; } = [];
        public ChannelReader<AsyncJobExecutionItem> Reader => _channel.Reader;
        public string ScratchDirectory { get; init; } = Path.GetTempPath();
        public bool Enqueue(AsyncJobExecutionItem item) => _channel.Writer.TryWrite(item);

        public ValueTask<bool> TryTransitionAsync(AsyncJobDescriptor next, CancellationToken ct)
        {
            lock (Transitions) { Transitions.Add(next); }
            return ValueTask.FromResult(true);
        }
    }

    [Fact]
    public async Task Worker_WithNonConcreteQueue_StillProcesses()
    {
        var scratch = Path.Combine(_dir, "fake");
        Directory.CreateDirectory(scratch);
        var queue = new FakeQueue { ScratchDirectory = scratch };
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(EmptyResult()));
        var manager = new AsyncQueryJobManager(_audit, environment: _env);
        var worker = new AsyncQueryJobBackgroundWorker(manager, queue, Scopes(sql), _audit, NullLogger<AsyncQueryJobBackgroundWorker>.Instance);
        var job = new AsyncJobDescriptor(Guid.NewGuid().ToString("D"), "t", "alice", "SELECT 1", "json", AsyncJobState.Queued, DateTimeOffset.UtcNow);
        queue.Enqueue(new AsyncJobExecutionItem(job, new AsyncQueryJobRequest("SELECT 1"), User(), new CancellationTokenSource()));

        await worker.StartAsync(CancellationToken.None);
        for (var i = 0; i < 100; i++)
        {
            lock (queue.Transitions)
            {
                if (queue.Transitions.Exists(d => d.State == AsyncJobState.Completed)) break;
            }
            await Task.Delay(50);
        }
        await worker.StopAsync(CancellationToken.None);

        lock (queue.Transitions)
        {
            queue.Transitions.ShouldContain(d => d.State == AsyncJobState.Running);
            queue.Transitions.ShouldContain(d => d.State == AsyncJobState.Completed);
        }
    }

    [Fact]
    public void Worker_WithoutQueue_ThrowsAtConstruction()
    {
        var manager = new AsyncQueryJobManager(_audit, environment: _env);
        Should.Throw<ArgumentNullException>(() => new AsyncQueryJobBackgroundWorker(
            manager, null!, Scopes(Substitute.For<IGovernedSqlExecutionService>()), _audit));
    }

    [Fact]
    public async Task CancelledJob_IsNotOverwrittenByLateCompletion()
    {
        var manager = new AsyncQueryJobManager(_audit, environment: _env);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                started.TrySetResult();
                await release.Task; // ignores the token: models a driver that finishes after the cancel
                return EmptyResult();
            });
        var worker = new AsyncQueryJobBackgroundWorker(manager, manager, Scopes(sql), _audit, NullLogger<AsyncQueryJobBackgroundWorker>.Instance);
        var job = await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1"), User(), new TenantId("t"));
        await worker.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        (await manager.CancelJobAsync(job.JobId, new TenantId("t"), "alice")).ShouldBeTrue();
        release.SetResult();
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        var final = await manager.GetJobAsync(job.JobId, new TenantId("t"), "alice");
        final!.State.ShouldBe(AsyncJobState.Cancelled);
        final.ResultFilePath.ShouldBeNull();
        Directory.GetFiles(manager.ScratchDirectory, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task TryTransition_FromTerminalState_IsRefused()
    {
        var manager = new AsyncQueryJobManager(_audit, environment: _env);
        var cancelled = new AsyncJobDescriptor("j", "t", "alice", "q", "json", AsyncJobState.Cancelled, DateTimeOffset.UtcNow);
        manager.UpdateJob(cancelled);

        (await manager.TryTransitionAsync(cancelled with { State = AsyncJobState.Completed }, CancellationToken.None)).ShouldBeFalse();
        (await manager.GetJobAsync("j", new TenantId("t"), "alice"))!.State.ShouldBe(AsyncJobState.Cancelled);
    }
}
