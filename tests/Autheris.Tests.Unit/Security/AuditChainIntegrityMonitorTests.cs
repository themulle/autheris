namespace Autheris.Tests.Unit.Security;

using System;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Health;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Security review 2026-10-06 (E-10, E-11): runtime chain verification and audit entries of the approval workflow.</summary>
public sealed class AuditChainIntegrityMonitorTests
{
    private static AuditChainIntegrityMonitor Monitor(IAuditLogRepository repo) =>
        new(repo, Options.Create(new GatewayOptions()), NullLogger<AuditChainIntegrityMonitor>.Instance);

    [Fact]
    public async Task VerifyNow_IntactChain_IsNotViolated()
    {
        var repo = Substitute.For<IAuditLogRepository>();
        repo.VerifyAuditHashChainAsync(Arg.Any<System.Threading.CancellationToken>()).Returns(true);
        var monitor = Monitor(repo);

        (await monitor.VerifyNowAsync()).ShouldBeTrue();

        monitor.IsViolated.ShouldBeFalse();
        monitor.LastRunFailed.ShouldBeFalse();
        monitor.LastCheckedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task VerifyNow_BrokenChain_IsViolated_UntilALaterRunIsIntact()
    {
        var repo = Substitute.For<IAuditLogRepository>();
        repo.VerifyAuditHashChainAsync(Arg.Any<System.Threading.CancellationToken>()).Returns(false, true);
        var monitor = Monitor(repo);

        (await monitor.VerifyNowAsync()).ShouldBeFalse();
        monitor.IsViolated.ShouldBeTrue();

        (await monitor.VerifyNowAsync()).ShouldBeTrue();
        monitor.IsViolated.ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyNow_RepositoryThrows_IsNotReportedAsViolation()
    {
        var repo = Substitute.For<IAuditLogRepository>();
        repo.VerifyAuditHashChainAsync(Arg.Any<System.Threading.CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("db down"));
        var monitor = Monitor(repo);

        (await monitor.VerifyNowAsync()).ShouldBeFalse();

        monitor.LastRunFailed.ShouldBeTrue();
        monitor.IsViolated.ShouldBeFalse(); // unknown state must not look like tampering
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VerifyNow_ViolationThenError_StaysViolated_UntilAnIntactRun()
    {
        var repo = Substitute.For<IAuditLogRepository>();
        var calls = 0;
        repo.VerifyAuditHashChainAsync(Arg.Any<System.Threading.CancellationToken>())
            .Returns(_ => ++calls switch
            {
                1 => Task.FromResult(false),
                2 => throw new InvalidOperationException("db down"),
                _ => Task.FromResult(true)
            });
        var monitor = Monitor(repo);

        (await monitor.VerifyNowAsync()).ShouldBeFalse();
        monitor.IsViolated.ShouldBeTrue();

        // A failing run must not clear the detected violation.
        (await monitor.VerifyNowAsync()).ShouldBeFalse();
        monitor.IsViolated.ShouldBeTrue();
        monitor.LastRunFailed.ShouldBeTrue();

        (await monitor.VerifyNowAsync()).ShouldBeTrue();
        monitor.IsViolated.ShouldBeFalse();
        monitor.LastRunFailed.ShouldBeFalse();
    }

    private static Task<GatewayHealthReport> CheckHealthAsync(AuditChainIntegrityMonitor monitor) =>
        new GatewayHealthCheckService(
            Options.Create(new GatewayOptions()),
            NullLogger<GatewayHealthCheckService>.Instance,
            auditChainMonitor: monitor).CheckHealthAsync();

    private static HealthCheckComponentResult AuditChainComponent(GatewayHealthReport report) =>
        System.Linq.Enumerable.Single(report.Components, c => c.Name == "AuditChain");

    [Fact]
    [Trait("Category", "Security")]
    public async Task Health_ViolationThenError_ReadinessStaysUnhealthy()
    {
        var repo = Substitute.For<IAuditLogRepository>();
        var calls = 0;
        repo.VerifyAuditHashChainAsync(Arg.Any<System.Threading.CancellationToken>())
            .Returns(_ => ++calls == 1 ? Task.FromResult(false) : throw new InvalidOperationException("db down"));
        var monitor = Monitor(repo);
        await monitor.VerifyNowAsync();
        await monitor.VerifyNowAsync();

        var report = await CheckHealthAsync(monitor);

        report.IsHealthy.ShouldBeFalse();
        AuditChainComponent(report).IsHealthy.ShouldBeFalse();
        AuditChainComponent(report).Description!.ShouldContain("FAILED");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Health_VerificationError_IsUnhealthyUnknown_WithoutExceptionText()
    {
        var repo = Substitute.For<IAuditLogRepository>();
        repo.VerifyAuditHashChainAsync(Arg.Any<System.Threading.CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("Host=db-internal;Password=secret"));
        var monitor = Monitor(repo);
        await monitor.VerifyNowAsync();

        var report = await CheckHealthAsync(monitor);

        report.IsHealthy.ShouldBeFalse();
        var component = AuditChainComponent(report);
        component.IsHealthy.ShouldBeFalse();
        component.Description!.ShouldContain("unknown");
        component.Description.ShouldNotContain("db-internal");
        component.Description.ShouldNotContain("secret");
    }

    [Fact]
    public async Task Health_IntactChain_IsHealthy()
    {
        var repo = Substitute.For<IAuditLogRepository>();
        repo.VerifyAuditHashChainAsync(Arg.Any<System.Threading.CancellationToken>()).Returns(true);
        var monitor = Monitor(repo);
        await monitor.VerifyNowAsync();

        var report = await CheckHealthAsync(monitor);

        AuditChainComponent(report).IsHealthy.ShouldBeTrue();
        report.IsHealthy.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Health_GovernanceDbFailure_DoesNotReturnExceptionText()
    {
        var governance = Substitute.For<IGovernanceRepository>();
        governance.GetAllTablesAsync(Arg.Any<System.Threading.CancellationToken>())
            .Returns<Task<System.Collections.Generic.IReadOnlyList<TableMetadata>>>(_ => throw new InvalidOperationException("Server=db-internal;Password=secret"));

        var report = await new GatewayHealthCheckService(
            Options.Create(new GatewayOptions()),
            NullLogger<GatewayHealthCheckService>.Instance,
            governanceRepository: governance).CheckHealthAsync();

        var component = System.Linq.Enumerable.Single(report.Components, c => c.Name == "GovernanceDb");
        component.IsHealthy.ShouldBeFalse();
        component.Description!.ShouldNotContain("db-internal");
        component.Description.ShouldNotContain("secret");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SystemMetrics_HealthServiceThrows_DetailsAreFixedMessage()
    {
        var health = Substitute.For<IGatewayHealthCheckService>();
        health.CheckHealthAsync(Arg.Any<System.Threading.CancellationToken>())
            .Returns<Task<GatewayHealthReport>>(_ => throw new InvalidOperationException("Server=db-internal;Password=secret"));
        var resourceGroups = Substitute.For<Autheris.Application.ResourceGroups.IResourceGroupManager>();
        resourceGroups.GetMetrics().Returns(new ResourceGroupMetrics(DateTimeOffset.UtcNow, Array.Empty<ResourceGroupTierMetrics>()));

        var metrics = await new Autheris.Application.Observability.GatewaySystemMetricsService(
            resourceGroups,
            NullLogger<Autheris.Application.Observability.GatewaySystemMetricsService>.Instance,
            healthCheckService: health).CollectSystemMetricsAsync();

        var component = System.Linq.Enumerable.Single(metrics.Components, c => c.ComponentName == "HealthCheckService");
        component.Status.ShouldBe("Unhealthy");
        component.Details!.ShouldNotContain("db-internal");
        component.Details.ShouldNotContain("secret");
    }

    [Fact]
    public void BuildStepAudit_UsesApproverAsActor_AndRequestTenant()
    {
        var req = new ConsentRequest { RequesterSid = new Sid("S-REQ"), TenantId = new TenantId("tenant-a") };

        var entry = ConsentApprovalPolicy.BuildStepAudit(req, new Sid("S-APPROVER"), "CONSENT_APPROVAL_STEP", "APPROVED", "APPROVED", "alice@corp", null);

        entry.ActorSid.Value.ShouldBe("S-APPROVER");
        entry.TenantId.Value.ShouldBe("tenant-a");
        entry.EventType.ShouldBe("CONSENT_APPROVAL_STEP");
        entry.DetailsJson.ShouldContain("alice@corp");
    }

    [Fact]
    public void BuildStepAudit_TruncatesVeryLongReason()
    {
        var req = new ConsentRequest { RequesterSid = new Sid("S-REQ") };

        var entry = ConsentApprovalPolicy.BuildStepAudit(req, new Sid("S-A"), "CONSENT_REQUEST_REJECTED", "REJECTED", "REJECTED", null, new string('x', 5000));

        entry.DetailsJson.Length.ShouldBeLessThan(1000);
    }
}
