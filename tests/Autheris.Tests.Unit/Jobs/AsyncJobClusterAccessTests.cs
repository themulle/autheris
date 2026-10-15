namespace Autheris.Tests.Unit.Jobs;

using System;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Autheris.Application.Jobs.Services;
using Autheris.Application.State;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-ARC-A-12: async job records shared across replicas through the cluster state.</summary>
public sealed class AsyncJobClusterAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "autheris-a12c-" + Guid.NewGuid().ToString("N"));
    private readonly IAuditLogRepository _auditA = Substitute.For<IAuditLogRepository>();
    private readonly IAuditLogRepository _auditB = Substitute.For<IAuditLogRepository>();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-14T10:00:00Z"));
    private readonly InMemoryClusterStateProvider _state;
    private readonly TenantId _tenant = new("t");

    public AsyncJobClusterAccessTests()
    {
        Directory.CreateDirectory(_root);
        _state = new InMemoryClusterStateProvider(_time);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private AsyncQueryJobManager Node(
        IAuditLogRepository audit,
        string name,
        IDistributedClusterStateProvider? state = null,
        bool shared = false,
        string? scratch = null,
        int maxTimeout = 900)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(Path.Combine(_root, name));
        return new AsyncQueryJobManager(
            audit,
            environment: env,
            jobOptions: Options.Create(new AsyncJobOptions { ScratchDirectory = scratch ?? Path.Combine(_root, name, "scratch"), SharedScratchDirectory = shared, MaxTimeoutSeconds = maxTimeout }),
            clusterState: state ?? _state,
            topology: new ClusterStateTopology(ClusterStateMode.External),
            node: new AsyncJobNodeIdentity(),
            timeProvider: _time);
    }

    private static ClaimsPrincipal User(string sid = "alice", string tenant = "t") => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, sid), new Claim("sub", sid), new Claim("tenant_id", tenant)], "Test"));

    private async Task<AsyncJobDescriptor> SubmitAsync(AsyncQueryJobManager node, string query = "SELECT secret_column FROM x")
        => await node.SubmitJobAsync(new AsyncQueryJobRequest(query), User(), _tenant);

    [Fact]
    public async Task JobSubmittedOnNodeA_StatusVisibleOnNodeB()
    {
        var a = Node(_auditA, "a");
        var b = Node(_auditB, "b");
        var job = await SubmitAsync(a);

        var seen = await b.GetJobAsync(job.JobId, _tenant, "alice");

        seen.ShouldNotBeNull();
        seen.State.ShouldBe(AsyncJobState.Queued);
        seen.Query.ShouldBeEmpty();
        seen.ResultFilePath.ShouldBeNull();
    }

    [Fact]
    public async Task NodeB_ForeignOwner_Returns404_AndAuditsDeny()
    {
        var a = Node(_auditA, "a");
        var b = Node(_auditB, "b");
        var job = await SubmitAsync(a);

        var seen = await b.GetJobAsync(job.JobId, _tenant, "mallory");

        seen.ShouldBeNull();
        await _auditB.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.AsyncJobAccessDenied && e.Decision == "DENY" && e.ActorSid.Value == "mallory"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NodeB_ForeignTenant_Returns404()
    {
        var a = Node(_auditA, "a");
        var b = Node(_auditB, "b");
        var job = await SubmitAsync(a);

        (await b.GetJobAsync(job.JobId, new TenantId("other-tenant"), "alice")).ShouldBeNull();
    }

    [Fact]
    public async Task NodeB_DescriptorAbsent_ReturnsNull()
    {
        var b = Node(_auditB, "b");

        (await b.GetJobAsync(Guid.NewGuid().ToString("D"), _tenant, "alice")).ShouldBeNull();
        (await b.GetJobAsync("not-a-guid", _tenant, "alice")).ShouldBeNull();
    }

    private static IDistributedClusterStateProvider ThrowingState(bool onGet = true, bool onSet = true)
    {
        var state = Substitute.For<IDistributedClusterStateProvider>();
        if (onGet)
        {
            state.GetAsync<AsyncJobClusterRecord>(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns<ValueTask<AsyncJobClusterRecord?>>(_ => throw new ClusterStateUnavailableException());
        }

        if (onSet)
        {
            state.SetAsync(Arg.Any<string>(), Arg.Any<AsyncJobClusterRecord>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns<ValueTask>(_ => throw new ClusterStateUnavailableException());
        }

        return state;
    }

    [Fact]
    public async Task NodeB_StateUnavailable_Throws()
    {
        var b = Node(_auditB, "b", ThrowingState());

        await Should.ThrowAsync<ClusterStateUnavailableException>(
            () => b.GetJobAsync(Guid.NewGuid().ToString("D"), _tenant, "alice"));
        await Should.ThrowAsync<ClusterStateUnavailableException>(
            () => b.GetJobResultStreamAsync(Guid.NewGuid().ToString("D"), _tenant, "alice"));
    }

    [Fact]
    public async Task Submit_StateUnavailable_FailsClosed_NothingQueued()
    {
        var a = Node(_auditA, "a", ThrowingState(onGet: false));

        await Should.ThrowAsync<ClusterStateUnavailableException>(() => SubmitAsync(a));

        a.Reader.TryRead(out _).ShouldBeFalse();
        await _auditA.DidNotReceive().RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submit_StateUnavailable_DoesNotCountAgainstTenantCap()
    {
        var throwing = Node(_auditA, "a", ThrowingState(onGet: false));
        for (var i = 0; i < 3; i++)
        {
            await Should.ThrowAsync<ClusterStateUnavailableException>(() => SubmitAsync(throwing));
        }

        // The failed submits left no local entry behind.
        var healthy = Node(_auditA, "a2");
        (await SubmitAsync(healthy)).State.ShouldBe(AsyncJobState.Queued);
    }

    [Fact]
    public async Task ClusterRecord_ContainsNoQueryAndNoAbsolutePath()
    {
        var a = Node(_auditA, "a");
        var job = await SubmitAsync(a, "SELECT secret_column FROM x");
        var file = Path.Combine(a.ScratchDirectory, "t", $"{job.JobId}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "[]");
        await a.TryTransitionAsync(job with { State = AsyncJobState.Completed, CompletedAt = _time.GetUtcNow(), ResultFilePath = file }, CancellationToken.None);

        var record = await _state.GetAsync<AsyncJobClusterRecord>($"asyncjob:t:{job.JobId}");

        record.ShouldNotBeNull();
        record.State.ShouldBe(AsyncJobState.Completed);
        record.ResultFileName.ShouldBe($"{job.JobId}.json");
        var json = JsonSerializer.Serialize(record);
        json.ShouldNotContain("secret_column");
        json.ShouldNotContain(_root);
        json.ShouldNotContain("/");
    }

    private async Task<(AsyncQueryJobManager A, AsyncQueryJobManager B, AsyncJobDescriptor Job)> CompletedOnAAsync(bool shared)
    {
        var sharedDir = Path.Combine(_root, "shared");
        var a = Node(_auditA, "a", shared: shared, scratch: shared ? sharedDir : null);
        var b = Node(_auditB, "b", shared: shared, scratch: shared ? sharedDir : null);
        var job = await SubmitAsync(a);
        var file = Path.Combine(a.ScratchDirectory, "t", $"{job.JobId}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "[{\"id\":1}]");
        await a.TryTransitionAsync(job with { State = AsyncJobState.Completed, CompletedAt = _time.GetUtcNow(), ResultFilePath = file }, CancellationToken.None);
        return (a, b, job);
    }

    [Fact]
    public async Task NodeB_ResultWithoutSharedScratch_ThrowsResultUnavailable()
    {
        var (_, b, job) = await CompletedOnAAsync(shared: false);

        var ex = await Should.ThrowAsync<AsyncJobResultUnavailableException>(() => b.GetJobResultStreamAsync(job.JobId, _tenant, "alice"));
        ex.ErrorCode.ShouldBe("ASYNC_JOB_RESULT_NODE_LOCAL");
    }

    [Fact]
    public async Task SharedScratch_ResultServedFromNodeB()
    {
        var (_, b, job) = await CompletedOnAAsync(shared: true);

        await using var stream = (await b.GetJobResultStreamAsync(job.JobId, _tenant, "alice"))!;
        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync()).ShouldBe("[{\"id\":1}]");
    }

    [Fact]
    public async Task TamperedRecordFileName_IsRejected()
    {
        var (_, b, job) = await CompletedOnAAsync(shared: true);
        var record = (await _state.GetAsync<AsyncJobClusterRecord>($"asyncjob:t:{job.JobId}"))!;
        var outside = Path.Combine(_root, "outside-secret.json");
        File.WriteAllText(outside, "secret");

        foreach (var tampered in new[] { "../../outside-secret.json", "..\\outside-secret.json", "other-id.json", $"{job.JobId}.exe", Path.Combine(_root, "outside-secret.json") })
        {
            await _state.SetAsync($"asyncjob:t:{job.JobId}", record with { ResultFileName = tampered }, TimeSpan.FromHours(1));
            await Should.ThrowAsync<GatewaySecurityException>(() => b.GetJobResultStreamAsync(job.JobId, _tenant, "alice"));
        }

        await _auditB.Received().RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.AsyncJobDownloaded && e.Decision == "DENY"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OwnerNodeLost_StatusReportsFailedAfterDeadline()
    {
        var a = Node(_auditA, "a");
        var b = Node(_auditB, "b");
        var job = await SubmitAsync(a);

        (await b.GetJobAsync(job.JobId, _tenant, "alice"))!.State.ShouldBe(AsyncJobState.Queued);

        _time.Advance(TimeSpan.FromSeconds(900 + 61));
        var lost = await b.GetJobAsync(job.JobId, _tenant, "alice");

        lost!.State.ShouldBe(AsyncJobState.Failed);
        lost.ErrorMessage.ShouldBe("job owner node lost");
    }

    [Fact]
    public async Task Sweeper_StateUnavailable_DeletesOnlyByAge()
    {
        var node = Node(_auditA, "a", ThrowingState(), maxTimeout: 60);
        var recent = PlaceFile(node, TimeSpan.FromMinutes(10));
        var ancient = PlaceFile(node, TimeSpan.FromHours(30));

        await node.SweepOrphanedScratchFilesAsync(CancellationToken.None);

        File.Exists(recent).ShouldBeTrue("with the state unavailable only the retention age deletes");
        File.Exists(ancient).ShouldBeFalse();
    }

    [Fact]
    public async Task Sweeper_ClusterRecordExists_KeepsFile()
    {
        var a = Node(_auditA, "a", maxTimeout: 60);
        var b = Node(_auditB, "b", scratch: a.ScratchDirectory, maxTimeout: 60);
        var job = await SubmitAsync(a);
        var path = PlaceFile(b, TimeSpan.FromMinutes(10), job.JobId);
        var orphan = PlaceFile(b, TimeSpan.FromMinutes(10));

        await b.SweepOrphanedScratchFilesAsync(CancellationToken.None);

        File.Exists(path).ShouldBeTrue();
        File.Exists(orphan).ShouldBeFalse();
    }

    private static string PlaceFile(AsyncQueryJobManager node, TimeSpan age, string? id = null)
    {
        var dir = Path.Combine(node.ScratchDirectory, "t");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{id ?? Guid.NewGuid().ToString("D")}.json");
        File.WriteAllText(path, "[]");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void ClusterWideTopology_WithoutStateProvider_ThrowsAtConstruction()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(_root);

        Should.Throw<InvalidOperationException>(() => new AsyncQueryJobManager(
            _auditA, environment: env, topology: new ClusterStateTopology(ClusterStateMode.External)));
    }

    // ---- cross-node cancel ----

    private async Task<(AsyncQueryJobBackgroundWorker Worker, TaskCompletionSource<CancellationToken> Token)> StartWorkerAsync(AsyncQueryJobManager node)
    {
        var tokenSeen = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var ct = call.Arg<CancellationToken>();
                tokenSeen.TrySetResult(ct);
                await Task.Delay(Timeout.Infinite, ct);
                return AsyncJobWorkerConcurrencyTests.EmptyResult();
            });
        var worker = new AsyncQueryJobBackgroundWorker(
            node, node, AsyncJobWorkerConcurrencyTests.Scopes(sql), Substitute.For<IAuditLogRepository>(),
            NullLogger<AsyncQueryJobBackgroundWorker>.Instance,
            clusterState: _state, topology: new ClusterStateTopology(ClusterStateMode.External));
        await worker.StartAsync(CancellationToken.None);
        return (worker, tokenSeen);
    }

    [Fact]
    public async Task CancelOnNodeB_CancelsRunningJobOnNodeA()
    {
        var a = Node(_auditA, "a");
        var b = Node(_auditB, "b");
        var (worker, token) = await StartWorkerAsync(a);
        var job = await SubmitAsync(a);
        var ct = await token.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var ownerNode = (await _state.GetAsync<AsyncJobClusterRecord>($"asyncjob:t:{job.JobId}"))!.OwnerNodeId;

        (await b.CancelJobAsync(job.JobId, _tenant, "alice")).ShouldBeTrue();

        ct.IsCancellationRequested.ShouldBeTrue("the owner node must stop the running query");
        (await a.GetJobAsync(job.JobId, _tenant, "alice"))!.State.ShouldBe(AsyncJobState.Cancelled);
        var record = (await _state.GetAsync<AsyncJobClusterRecord>($"asyncjob:t:{job.JobId}"))!;
        record.State.ShouldBe(AsyncJobState.Cancelled);
        record.OwnerNodeId.ShouldBe(ownerNode);
        await _auditB.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.AsyncJobCancelled && e.Decision == "ALLOW"),
            Arg.Any<CancellationToken>());
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancelOnNodeB_ForeignOwner_IsDeniedAndAudited()
    {
        var a = Node(_auditA, "a");
        var b = Node(_auditB, "b");
        var job = await SubmitAsync(a);

        (await b.CancelJobAsync(job.JobId, _tenant, "mallory")).ShouldBeFalse();

        (await _state.GetAsync<AsyncJobClusterRecord>($"asyncjob:t:{job.JobId}"))!.State.ShouldBe(AsyncJobState.Queued);
        await _auditB.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.AsyncJobAccessDenied && e.Decision == "DENY"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelSignal_ForeignOwner_IsIgnoredByOwner()
    {
        var a = Node(_auditA, "a");
        var (worker, token) = await StartWorkerAsync(a);
        var job = await SubmitAsync(a);
        var ct = await token.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await _state.PublishEventAsync("asyncjob:cancel", new AsyncJobCancelSignal("t", job.JobId, "mallory"));
        await _state.PublishEventAsync("asyncjob:cancel", new AsyncJobCancelSignal("other-tenant", job.JobId, "alice"));

        ct.IsCancellationRequested.ShouldBeFalse();
        (await a.GetJobAsync(job.JobId, _tenant, "alice"))!.State.ShouldBe(AsyncJobState.Running);

        // The legitimate owner signal does cancel.
        await _state.PublishEventAsync("asyncjob:cancel", new AsyncJobCancelSignal("t", job.JobId, "alice"));
        ct.IsCancellationRequested.ShouldBeTrue();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LateCompletionOnOwner_DoesNotOverwriteRemoteCancel()
    {
        var a = Node(_auditA, "a");
        var b = Node(_auditB, "b");
        var job = await SubmitAsync(a);
        (await b.CancelJobAsync(job.JobId, _tenant, "alice")).ShouldBeTrue();
        var file = Path.Combine(a.ScratchDirectory, "t", $"{job.JobId}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "[]");

        var accepted = await a.TryTransitionAsync(job with { State = AsyncJobState.Completed, ResultFilePath = file }, CancellationToken.None);

        accepted.ShouldBeFalse();
        File.Exists(file).ShouldBeFalse();
        (await a.GetJobAsync(job.JobId, _tenant, "alice"))!.State.ShouldBe(AsyncJobState.Cancelled);
        (await _state.GetAsync<AsyncJobClusterRecord>($"asyncjob:t:{job.JobId}"))!.State.ShouldBe(AsyncJobState.Cancelled);
    }
}
