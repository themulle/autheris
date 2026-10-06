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
