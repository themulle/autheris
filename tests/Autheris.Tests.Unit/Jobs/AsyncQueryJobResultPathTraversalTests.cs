namespace Autheris.Tests.Unit.Jobs;

using System;
using System.IO;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Jobs.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// A result path outside the scratch directory is an authorization problem and must surface as a
/// <see cref="GatewaySecurityException"/> (mapped to 403), not as a plain SecurityException (mapped to 500).
/// </summary>
public sealed class AsyncQueryJobResultPathTraversalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "autheris-test-traversal-" + Guid.NewGuid().ToString("N"));

    public AsyncQueryJobResultPathTraversalTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task GetJobResultStreamAsync_ResultPathOutsideScratch_ThrowsGatewaySecurityExceptionWithStableCode()
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(_root);
        var manager = new AsyncQueryJobManager(Substitute.For<IAuditLogRepository>(), environment: environment);

        var outside = Path.Combine(_root, "secret.txt");
        await File.WriteAllTextAsync(outside, "secret");
        manager.UpdateJob(new AsyncJobDescriptor(
            "job-1", "tenant-a", "alice", "SELECT 1", "json", AsyncJobState.Completed, DateTimeOffset.UtcNow,
            ResultFilePath: outside));

        var ex = await Should.ThrowAsync<GatewaySecurityException>(
            () => manager.GetJobResultStreamAsync("job-1", new TenantId("tenant-a"), "alice"));

        ex.ErrorCode.ShouldBe("JOB_RESULT_PATH_FORBIDDEN");
    }

    [Fact]
    public async Task GetJobResultStreamAsync_SiblingDirectoryWithScratchPrefix_IsDenied()
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(_root);
        var audit = Substitute.For<IAuditLogRepository>();
        var manager = new AsyncQueryJobManager(audit, environment: environment);

        // .../scratch/jobs-evil/x shares the string prefix of .../scratch/jobs but is a different directory.
        var evilDir = manager.ScratchDirectory + "-evil";
        Directory.CreateDirectory(evilDir);
        var evilFile = Path.Combine(evilDir, "x");
        await File.WriteAllTextAsync(evilFile, "secret");
        manager.UpdateJob(new AsyncJobDescriptor(
            "job-2", "tenant-a", "alice", "SELECT 1", "json", AsyncJobState.Completed, DateTimeOffset.UtcNow,
            ResultFilePath: evilFile));

        var ex = await Should.ThrowAsync<GatewaySecurityException>(
            () => manager.GetJobResultStreamAsync("job-2", new TenantId("tenant-a"), "alice"));

        ex.ErrorCode.ShouldBe("JOB_RESULT_PATH_FORBIDDEN");
        await audit.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.Decision == "DENY"),
            Arg.Any<System.Threading.CancellationToken>());
    }

    [Fact]
    public async Task GetJobResultStreamAsync_FileInsideScratch_IsStreamed()
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(_root);
        var manager = new AsyncQueryJobManager(Substitute.For<IAuditLogRepository>(), environment: environment);

        var inside = Path.Combine(manager.ScratchDirectory, "ok.json");
        await File.WriteAllTextAsync(inside, "{}");
        manager.UpdateJob(new AsyncJobDescriptor(
            "job-3", "tenant-a", "alice", "SELECT 1", "json", AsyncJobState.Completed, DateTimeOffset.UtcNow,
            ResultFilePath: inside));

        await using var stream = await manager.GetJobResultStreamAsync("job-3", new TenantId("tenant-a"), "alice");

        stream.ShouldNotBeNull();
    }
}
