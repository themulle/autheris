namespace Autheris.Tests.Unit.Jobs;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Channels;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Jobs.Interfaces;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Jobs.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
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

        public ValueTask SweepOrphanedScratchFilesAsync(CancellationToken ct) => ValueTask.CompletedTask;
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

    // ---- scheduler ----

    private static AsyncJobExecutionItem Item(string tenant, string? id = null)
    {
        var job = new AsyncJobDescriptor(id ?? Guid.NewGuid().ToString("D"), tenant, "alice", "SELECT 1", "json", AsyncJobState.Queued, DateTimeOffset.UtcNow);
        return new AsyncJobExecutionItem(job, new AsyncQueryJobRequest("SELECT 1"), User("alice", tenant), new CancellationTokenSource());
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not reached.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task MaxConcurrentJobs_IsHonoured()
    {
        var channel = Channel.CreateUnbounded<AsyncJobExecutionItem>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0, peak = 0, finished = 0;
        using var scheduler = new AsyncJobScheduler(2, 2, async (_, _) =>
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            await gate.Task;
            Interlocked.Decrement(ref running);
            Interlocked.Increment(ref finished);
        }, NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        var loop = scheduler.RunAsync(channel.Reader, cts.Token);
        for (var i = 0; i < 6; i++) channel.Writer.TryWrite(Item("t" + i));

        await WaitUntilAsync(() => Volatile.Read(ref running) == 2);
        await Task.Delay(200);
        Volatile.Read(ref peak).ShouldBe(2);
        gate.SetResult();
        await WaitUntilAsync(() => Volatile.Read(ref finished) == 6);
        Volatile.Read(ref peak).ShouldBe(2);
        cts.Cancel();
        await loop;
    }

    [Fact]
    public async Task SlowJobOfTenantA_DoesNotBlockJobOfTenantB()
    {
        var channel = Channel.CreateUnbounded<AsyncJobExecutionItem>();
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new AsyncJobScheduler(4, 1, async (item, _) =>
        {
            if (item.Job.TenantId == "a") await slow.Task; else bDone.TrySetResult();
        }, NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        var loop = scheduler.RunAsync(channel.Reader, cts.Token);

        channel.Writer.TryWrite(Item("a"));
        channel.Writer.TryWrite(Item("a"));
        channel.Writer.TryWrite(Item("b"));

        await bDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        slow.SetResult();
        cts.Cancel();
        await loop;
    }

    [Fact]
    public async Task TenantAtCap_DoesNotOccupyGlobalSlot()
    {
        var channel = Channel.CreateUnbounded<AsyncJobExecutionItem>();
        var gateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var scheduler = new AsyncJobScheduler(2, 1, async (item, _) =>
        {
            started.Enqueue(item.Job.TenantId);
            if (item.Job.TenantId == "a") await gateA.Task;
        }, NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        var loop = scheduler.RunAsync(channel.Reader, cts.Token);

        // a1 runs, a2 and a3 are over the tenant cap and wait without holding a global slot: b and c still run.
        foreach (var t in new[] { "a", "a", "a", "b", "c" }) channel.Writer.TryWrite(Item(t));

        await WaitUntilAsync(() => started.Contains("b") && started.Contains("c"));
        started.Count(t => t == "a").ShouldBe(1);

        gateA.SetResult();
        await WaitUntilAsync(() => started.Count(t => t == "a") == 3);
        cts.Cancel();
        await loop;
    }

    // ---- options and queue cap ----

    [Fact]
    public void AsyncJobOptionsValidator_RejectsPerTenantAboveGlobal()
    {
        var validator = new AsyncJobOptionsValidator();

        validator.Validate(null, new AsyncJobOptions { MaxConcurrentJobs = 2, MaxConcurrentJobsPerTenant = 3 }).Failed.ShouldBeTrue();
        validator.Validate(null, new AsyncJobOptions { MaxConcurrentJobs = 0 }).Failed.ShouldBeTrue();
        validator.Validate(null, new AsyncJobOptions { MaxQueuedJobsPerTenant = 0 }).Failed.ShouldBeTrue();
        validator.Validate(null, new AsyncJobOptions { DefaultTimeoutSeconds = 100, MaxTimeoutSeconds = 50 }).Failed.ShouldBeTrue();
        validator.Validate(null, new AsyncJobOptions { ScratchDirectory = "relative/dir" }).Failed.ShouldBeTrue();
        validator.Validate(null, new AsyncJobOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Submit_AboveQueuedPerTenantCap_IsRejected()
    {
        var jobOptions = Options.Create(new AsyncJobOptions { MaxQueuedJobsPerTenant = 2 });
        var manager = new AsyncQueryJobManager(_audit, environment: _env, jobOptions: jobOptions);

        await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1"), User(), new TenantId("t"));
        await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1"), User(), new TenantId("t"));

        await Should.ThrowAsync<AsyncJobQueueLimitExceededException>(
            () => manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1"), User(), new TenantId("t")));
        // Another tenant is unaffected.
        await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1"), User("alice", "other"), new TenantId("other"));
    }

    // ---- scratch directory and sweeper ----

    [Fact]
    public void ConfiguredScratchDirectory_IsUsed()
    {
        var configured = Path.Combine(_dir, "configured-scratch");
        var manager = new AsyncQueryJobManager(_audit, environment: _env,
            jobOptions: Options.Create(new AsyncJobOptions { ScratchDirectory = configured }));

        manager.ScratchDirectory.ShouldBe(configured);
        Directory.Exists(configured).ShouldBeTrue();
    }

    private static string PlaceFile(string scratch, string tenant, string id, string ext, TimeSpan age)
    {
        var dir = Path.Combine(scratch, tenant);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{id}.{ext}");
        File.WriteAllText(path, "[]");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public async Task Sweeper_RemovesOrphanedScratchFiles()
    {
        var jobOptions = Options.Create(new AsyncJobOptions { MaxTimeoutSeconds = 60, RetentionHours = 24 });
        var manager = new AsyncQueryJobManager(_audit, environment: _env, jobOptions: jobOptions);
        var orphanOld = PlaceFile(manager.ScratchDirectory, "t", Guid.NewGuid().ToString("D"), "json", TimeSpan.FromMinutes(10));
        var orphanFresh = PlaceFile(manager.ScratchDirectory, "t", Guid.NewGuid().ToString("D"), "csv", TimeSpan.FromSeconds(5));
        var ancient = PlaceFile(manager.ScratchDirectory, "t", Guid.NewGuid().ToString("D"), "json", TimeSpan.FromHours(30));
        var foreign = Path.Combine(manager.ScratchDirectory, "t", "not-a-job.txt");
        File.WriteAllText(foreign, "x");
        File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-5));

        await manager.SweepOrphanedScratchFilesAsync(CancellationToken.None);

        File.Exists(orphanOld).ShouldBeFalse();
        File.Exists(orphanFresh).ShouldBeTrue("a fresh file may belong to a job of another replica");
        File.Exists(ancient).ShouldBeFalse();
        File.Exists(foreign).ShouldBeTrue("only {guid}.csv|json files are ever swept");
    }

    [Fact]
    public async Task Sweeper_KeepsFilesOfKnownJobs()
    {
        var jobOptions = Options.Create(new AsyncJobOptions { MaxTimeoutSeconds = 60, RetentionHours = 24 });
        var manager = new AsyncQueryJobManager(_audit, environment: _env, jobOptions: jobOptions);
        var id = Guid.NewGuid().ToString("D");
        var path = PlaceFile(manager.ScratchDirectory, "t", id, "json", TimeSpan.FromMinutes(10));
        manager.UpdateJob(new AsyncJobDescriptor(id, "t", "alice", "q", "json", AsyncJobState.Completed, DateTimeOffset.UtcNow, ResultFilePath: path));

        await manager.SweepOrphanedScratchFilesAsync(CancellationToken.None);

        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public async Task Sweeper_DoesNotFollowSymlinks()
    {
        if (OperatingSystem.IsWindows()) return;
        var jobOptions = Options.Create(new AsyncJobOptions { MaxTimeoutSeconds = 60 });
        var manager = new AsyncQueryJobManager(_audit, environment: _env, jobOptions: jobOptions);
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, $"{Guid.NewGuid():D}.json");
        File.WriteAllText(victim, "keep");
        File.SetLastWriteTimeUtc(victim, DateTime.UtcNow.AddDays(-3));
        Directory.CreateSymbolicLink(Path.Combine(manager.ScratchDirectory, "linked-tenant"), outside);

        await manager.SweepOrphanedScratchFilesAsync(CancellationToken.None);

        File.Exists(victim).ShouldBeTrue();
    }

    [Fact]
    public async Task Worker_RunsSweeperAtStart()
    {
        var jobOptions = Options.Create(new AsyncJobOptions { MaxTimeoutSeconds = 60 });
        var manager = new AsyncQueryJobManager(_audit, environment: _env, jobOptions: jobOptions);
        var orphan = PlaceFile(manager.ScratchDirectory, "t", Guid.NewGuid().ToString("D"), "json", TimeSpan.FromHours(1));
        var worker = new AsyncQueryJobBackgroundWorker(manager, manager, Scopes(Substitute.For<IGovernedSqlExecutionService>()), _audit,
            NullLogger<AsyncQueryJobBackgroundWorker>.Instance, jobOptions: jobOptions);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => !File.Exists(orphan));
        await worker.StopAsync(CancellationToken.None);
    }
}
