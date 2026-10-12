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
            RewrittenSql: "SELECT id, mask(email) FROM users",
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
}
