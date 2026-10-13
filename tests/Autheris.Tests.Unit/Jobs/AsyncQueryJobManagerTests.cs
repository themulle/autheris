namespace Autheris.Tests.Unit.Jobs;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Jobs.Interfaces;
using Autheris.Application.Jobs.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class AsyncQueryJobManagerTests : IDisposable
{
    private readonly string _testScratchDir;
    private readonly IHostEnvironment _environment;
    private readonly IAuditLogRepository _auditLogRepo;

    public AsyncQueryJobManagerTests()
    {
        _testScratchDir = Path.Combine(Path.GetTempPath(), "autheris-test-scratch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testScratchDir);

        _environment = Substitute.For<IHostEnvironment>();
        _environment.ContentRootPath.Returns(_testScratchDir);

        _auditLogRepo = Substitute.For<IAuditLogRepository>();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testScratchDir))
            {
                Directory.Delete(_testScratchDir, recursive: true);
            }
        }
        catch { }
    }

    private static ClaimsPrincipal CreateUser(string userSid, string tenantId = "tenant-a")
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userSid),
            new Claim("sub", userSid),
            new Claim("tenant_id", tenantId)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    [Fact]
    public async Task SubmitJob_EnqueuesSuccessfully_AndIsRetrievable()
    {
        // Arrange
        var manager = new AsyncQueryJobManager(_auditLogRepo, environment: _environment);
        var user = CreateUser("alice", "tenant-a");
        var request = new AsyncQueryJobRequest("SELECT * FROM sales.orders", Format: "json");

        // Act
        var descriptor = await manager.SubmitJobAsync(request, user, new TenantId("tenant-a"));

        // Assert
        descriptor.ShouldNotBeNull();
        descriptor.JobId.ShouldNotBeNullOrWhiteSpace();
        descriptor.TenantId.ShouldBe("tenant-a");
        descriptor.SubmittedByUserId.ShouldBe("alice");
        descriptor.Query.ShouldBe("SELECT * FROM sales.orders");
        descriptor.Format.ShouldBe("json");
        descriptor.State.ShouldBe(AsyncJobState.Queued);

        var retrieved = await manager.GetJobAsync(descriptor.JobId, new TenantId("tenant-a"), "alice");
        retrieved.ShouldNotBeNull();
        retrieved.JobId.ShouldBe(descriptor.JobId);
    }

    [Fact]
    public async Task GetJob_ZeroIdor_DifferentTenantOrUser_ReturnsNull()
    {
        // Arrange
        var manager = new AsyncQueryJobManager(_auditLogRepo, environment: _environment);
        var user = CreateUser("alice", "tenant-a");
        var request = new AsyncQueryJobRequest("SELECT 1");
        var descriptor = await manager.SubmitJobAsync(request, user, new TenantId("tenant-a"));

        // Act & Assert 1: Cross-tenant access returns null (404)
        var crossTenant = await manager.GetJobAsync(descriptor.JobId, new TenantId("tenant-b"), "alice");
        crossTenant.ShouldBeNull();

        // Act & Assert 2: Different user within same tenant returns null (404)
        var crossUser = await manager.GetJobAsync(descriptor.JobId, new TenantId("tenant-a"), "mallory");
        crossUser.ShouldBeNull();
    }

    [Fact]
    public async Task GetJob_PathTraversalJobId_ThrowsArgumentException()
    {
        // Arrange
        var manager = new AsyncQueryJobManager(_auditLogRepo, environment: _environment);

        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(() =>
            manager.GetJobAsync("../../etc/passwd", new TenantId("tenant-a"), "alice"));

        await Should.ThrowAsync<ArgumentException>(() =>
            manager.GetJobAsync("job/sub", new TenantId("tenant-a"), "alice"));

        await Should.ThrowAsync<ArgumentException>(() =>
            manager.GetJobAsync("job\\sub", new TenantId("tenant-a"), "alice"));
    }

    [Fact]
    public async Task CancelJob_AbortsRunningExecution_AndUpdatesState()
    {
        // Arrange
        var manager = new AsyncQueryJobManager(_auditLogRepo, environment: _environment);
        var user = CreateUser("alice", "tenant-a");
        var request = new AsyncQueryJobRequest("SELECT * FROM huge_table");
        var descriptor = await manager.SubmitJobAsync(request, user, new TenantId("tenant-a"));

        // Act
        var cancelled = await manager.CancelJobAsync(descriptor.JobId, new TenantId("tenant-a"), "alice");

        // Assert
        cancelled.ShouldBeTrue();
        var updated = await manager.GetJobAsync(descriptor.JobId, new TenantId("tenant-a"), "alice");
        updated.ShouldNotBeNull();
        updated.State.ShouldBe(AsyncJobState.Cancelled);
        updated.ErrorMessage.ShouldNotBeNull();
        updated.ErrorMessage.ShouldContain("cancelled");

        // Calling cancel again on cancelled job returns false
        var repeatCancel = await manager.CancelJobAsync(descriptor.JobId, new TenantId("tenant-a"), "alice");
        repeatCancel.ShouldBeFalse();
    }

    [Fact]
    public async Task PurgeExpiredJobs_RemovesTerminalOldJobs()
    {
        // Arrange
        var manager = new AsyncQueryJobManager(_auditLogRepo, environment: _environment);
        var user = CreateUser("alice", "tenant-a");
        var request = new AsyncQueryJobRequest("SELECT 1");
        var descriptor = await manager.SubmitJobAsync(request, user, new TenantId("tenant-a"));

        // Simulate completed job 3 hours ago
        var oldCompleted = descriptor with
        {
            State = AsyncJobState.Completed,
            CompletedAt = DateTimeOffset.UtcNow.AddHours(-3)
        };
        manager.UpdateJob(oldCompleted);

        // Act: purge jobs older than 2 hours
        await manager.PurgeExpiredJobsAsync(TimeSpan.FromHours(2));

        // Assert
        var purged = await manager.GetJobAsync(descriptor.JobId, new TenantId("tenant-a"), "alice");
        purged.ShouldBeNull();
    }

    private async Task<AsyncJobDescriptor?> RunWorkerWithFailingSqlAsync(Exception failure)
    {
        var manager = new AsyncQueryJobManager(_auditLogRepo, environment: _environment);
        var sqlService = Substitute.For<IGovernedSqlExecutionService>();
        sqlService.ExecuteQueryBufferedAsync(
            Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GovernedSqlResult>(failure));
        var services = new ServiceCollection();
        services.AddScoped(_ => sqlService);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var worker = new AsyncQueryJobBackgroundWorker(manager, scopeFactory, _auditLogRepo, logger: NullLogger<AsyncQueryJobBackgroundWorker>.Instance);
        var descriptor = await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELEC 1", Format: "json"), CreateUser("alice"), new TenantId("tenant-a"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);
        AsyncJobDescriptor? job = null;
        for (int i = 0; i < 100; i++)
        {
            job = await manager.GetJobAsync(descriptor.JobId, new TenantId("tenant-a"), "alice");
            if (job is { State: AsyncJobState.Failed or AsyncJobState.Cancelled or AsyncJobState.Completed }) break;
            await Task.Delay(50);
        }

        await worker.StopAsync(CancellationToken.None);
        return job;
    }

    [Fact]
    public async Task BackgroundWorker_StatementThatDoesNotParse_IsFailedWithTheSyntaxMessage_NotCancelled()
    {
        var job = await RunWorkerWithFailingSqlAsync(new Antlr4.Runtime.Misc.ParseCanceledException("line 1:0: mismatched input"));

        job.ShouldNotBeNull();
        job.State.ShouldBe(AsyncJobState.Failed);
        job.ErrorMessage.ShouldBe(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.SyntaxError));
    }

    [Fact]
    public async Task BackgroundWorker_ParserTimeout_IsFailedWithTheLimitMessage_NotCancelled()
    {
        var job = await RunWorkerWithFailingSqlAsync(new Antlr4.Runtime.Misc.ParseCanceledException("budget", new TimeoutException()));

        job.ShouldNotBeNull();
        job.State.ShouldBe(AsyncJobState.Failed);
        job.ErrorMessage.ShouldBe(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.LimitExceeded));
    }

    [Fact]
    public async Task BackgroundWorker_PlainCancellation_StaysCancelled()
    {
        var job = await RunWorkerWithFailingSqlAsync(new OperationCanceledException());

        job.ShouldNotBeNull();
        job.State.ShouldBe(AsyncJobState.Cancelled);
    }

    [Fact]
    public async Task BackgroundWorker_ExecutesGovernedSql_AndProducesResultFileWithDdm()
    {
        // Arrange
        var manager = new AsyncQueryJobManager(_auditLogRepo, environment: _environment);
        var user = CreateUser("alice", "tenant-a");

        var sqlService = Substitute.For<IGovernedSqlExecutionService>();
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["email"] = "a***@example.com" }
        };
        var governedResult = new GovernedSqlResult(
            OriginalSql: "SELECT id, email FROM users",
            Columns: new[] { "id", "email" },
            Rows: rows,
            RowCount: 1,
            ElapsedMilliseconds: 15);

        sqlService.ExecuteQueryBufferedAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<TenantId>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(governedResult));

        var services = new ServiceCollection();
        services.AddScoped(_ => sqlService);
        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var worker = new AsyncQueryJobBackgroundWorker(
            manager,
            scopeFactory,
            _auditLogRepo,
            logger: NullLogger<AsyncQueryJobBackgroundWorker>.Instance);

        var request = new AsyncQueryJobRequest("SELECT id, email FROM users", Format: "json");
        var descriptor = await manager.SubmitJobAsync(request, user, new TenantId("tenant-a"));

        // Act: run worker loop in background
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var workerTask = worker.StartAsync(cts.Token);

        // Wait until job is completed
        AsyncJobDescriptor? completedJob = null;
        for (int i = 0; i < 50; i++)
        {
            var job = await manager.GetJobAsync(descriptor.JobId, new TenantId("tenant-a"), "alice");
            if (job?.State == AsyncJobState.Completed)
            {
                completedJob = job;
                break;
            }
            await Task.Delay(50);
        }

        await worker.StopAsync(CancellationToken.None);

        // Assert
        completedJob.ShouldNotBeNull();
        completedJob.State.ShouldBe(AsyncJobState.Completed);
        completedJob.RowsProduced.ShouldBe(1);
        completedJob.BytesProduced.ShouldNotBeNull();
        completedJob.BytesProduced.Value.ShouldBeGreaterThan(0);
        completedJob.ResultFilePath.ShouldNotBeNull();

        // Download result stream
        using var stream = await manager.GetJobResultStreamAsync(descriptor.JobId, new TenantId("tenant-a"), "alice");
        stream.ShouldNotBeNull();
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();
        content.ShouldContain("a***@example.com");
    }

    [Fact]
    public async Task GetJob_ForeignOwnerOrTenant_RecordsDenyAuditWithoutLeakingOwner()
    {
        var recorded = new List<AuditLogEntry>();
        _auditLogRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(recorded.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var manager = new AsyncQueryJobManager(_auditLogRepo, environment: _environment);
        var descriptor = await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1"), CreateUser("alice", "tenant-a"), new TenantId("tenant-a"));
        recorded.Clear();

        var result = await manager.GetJobAsync(descriptor.JobId, new TenantId("tenant-a"), "mallory");

        result.ShouldBeNull();
        var deny = recorded.ShouldHaveSingleItem();
        deny.Decision.ShouldBe("DENY");
        deny.EventType.ShouldBe("ASYNC_JOB_ACCESS_DENIED");
        deny.TargetTable.ShouldBe($"job:{descriptor.JobId}");
        deny.ActorSid.Value.ShouldBe("mallory");
        deny.TenantId.Value.ShouldBe("tenant-a");
        deny.DetailsJson.ShouldNotContain("alice");

        recorded.Clear();
        (await manager.GetJobAsync(descriptor.JobId, new TenantId("tenant-b"), "alice")).ShouldBeNull();
        var crossTenant = recorded.ShouldHaveSingleItem();
        crossTenant.TenantId.Value.ShouldBe("tenant-b");
        crossTenant.DetailsJson.ShouldNotContain("tenant-a");
    }

    [Fact]
    public async Task GetJobResultStream_WhenAllowAuditFails_ThrowsAndReturnsNoStream()
    {
        var manager = new AsyncQueryJobManager(_auditLogRepo, environment: _environment);
        var descriptor = await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1"), CreateUser("alice", "tenant-a"), new TenantId("tenant-a"));
        var resultFile = Path.Combine(manager.ScratchDirectory, descriptor.JobId + ".json");
        await File.WriteAllTextAsync(resultFile, "[]");
        manager.UpdateJob(descriptor with { State = AsyncJobState.Completed, ResultFilePath = resultFile });

        _auditLogRepo.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("audit store down")));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            manager.GetJobResultStreamAsync(descriptor.JobId, new TenantId("tenant-a"), "alice"));
    }

    private static ClaimsPrincipal CreateUserWithSidClaims(string primarySid, string sub, string tenantId = "tenant-a") =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.PrimarySid, primarySid),
            new Claim("sub", sub),
            new Claim("tenant_id", tenantId)
        }, "TestAuth"));

    [Fact]
    public async Task SubmitJob_OwnerWithPrimarySid_IsAccessibleWithCanonicalOwnerResolution()
    {
        var manager = new AsyncQueryJobManager(_auditLogRepo, environment: _environment);
        var owner = CreateUserWithSidClaims("S-1-5-21-ALICE", "sub-alice");

        var descriptor = await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1"), owner, new TenantId("tenant-a"));

        // The endpoints resolve the caller with AsyncJobOwner.Resolve for status/result/cancel.
        descriptor.SubmittedByUserId.ShouldBe("S-1-5-21-ALICE");
        var ownerKey = AsyncJobOwner.Resolve(owner)!;
        (await manager.GetJobAsync(descriptor.JobId, new TenantId("tenant-a"), ownerKey)).ShouldNotBeNull();
        (await manager.CancelJobAsync(descriptor.JobId, new TenantId("tenant-a"), ownerKey)).ShouldBeTrue();
    }

    [Fact]
    public async Task SubmitJob_DifferentPrincipalSharingOnlySub_CannotAccessJob()
    {
        var manager = new AsyncQueryJobManager(_auditLogRepo, environment: _environment);
        var owner = CreateUserWithSidClaims("S-1-5-21-ALICE", "shared-sub");
        var other = CreateUserWithSidClaims("S-1-5-21-MALLORY", "shared-sub");

        var descriptor = await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1"), owner, new TenantId("tenant-a"));

        (await manager.GetJobAsync(descriptor.JobId, new TenantId("tenant-a"), AsyncJobOwner.Resolve(other)!)).ShouldBeNull();
        (await manager.CancelJobAsync(descriptor.JobId, new TenantId("tenant-a"), AsyncJobOwner.Resolve(other)!)).ShouldBeFalse();
    }

    [Fact]
    public async Task SubmitJob_PrincipalWithoutIdentity_IsRejected()
    {
        var manager = new AsyncQueryJobManager(_auditLogRepo, environment: _environment);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tenant_id", "tenant-a") }, "TestAuth"));

        var ex = await Should.ThrowAsync<Autheris.Domain.Exceptions.GatewaySecurityException>(() =>
            manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1"), anonymous, new TenantId("tenant-a")));
        ex.ErrorCode.ShouldBe("ASYNC_JOB_OWNER_UNRESOLVABLE");
    }
}
